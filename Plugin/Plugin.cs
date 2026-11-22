using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using NLog;
using Sandbox;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.API.Session;
using Torch.Managers;
using Torch.Views;
using VRage.Game.ModAPI;

namespace CometWorks.ServerList.Torch;

public sealed class Plugin : TorchPluginBase, IWpfPlugin
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly Guid NexusV3 = new("28a12184-0422-43ba-a6e6-2e228611cca6");
    private readonly object sync = new();
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly NexusIntegration nexus = new();
    private CancellationTokenSource transfers = new();
    private Persistent<PluginConfig> config = null!;
    private Task? worker;
    private Outbound? outbound;
    private DateTimeOffset nextCapture;
    private ITorchSessionManager sessions = null!;
    private bool disposed;

    public UserControl GetControl() => new PropertyGrid { DataContext = config.Data };
    private sealed record Outbound(Listing Listing, Admin[] Admins);

    public override void Init(ITorchBase torch)
    {
        base.Init(torch);
        config = Persistent<PluginConfig>.Load(Path.Combine(StoragePath, "ServerList.cfg"));
        config.Data.PropertyChanged += ConfigChanged;
        sessions = torch.Managers.GetManager<ITorchSessionManager>();
        sessions.SessionStateChanged += SessionChanged;
        worker = Task.Run(RunAsync);
        Log.Info("Server list ready. Enable sharing and set your public join address in the plugin configuration.");
    }

    private void SessionChanged(ITorchSession session, TorchSessionState state)
    {
        lock (sync)
        {
            if (disposed) return;
            if (state is TorchSessionState.Unloading or TorchSessionState.Unloaded) nexus.Dispose();
            SetOutbound(null);
            nextCapture = default;
        }
    }

    private void ConfigChanged(object sender, PropertyChangedEventArgs args)
    {
        lock (sync)
        {
            if (disposed) return;
            SetOutbound(null);
            nextCapture = default;
            config.Save();
        }
    }

    public override void Update()
    {
        lock (sync)
        {
            if (disposed || sessions.CurrentSession?.State != TorchSessionState.Loaded) return;
            var now = DateTimeOffset.UtcNow;
            if (now < nextCapture) return;
            nextCapture = now.AddSeconds(15);
            var c = config.Data;
            if (!c.Enabled)
            {
                SetOutbound(null);
                return;
            }
            var name = string.IsNullOrWhiteSpace(c.Name) ? MySandboxGame.ConfigDedicated.ServerName : c.Name;
            var admins = MySandboxGame.ConfigDedicated.Administrators.Where(id => id is { Length: 17 }
                && id.All(c => c is >= '0' and <= '9')).Distinct().OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => new Admin("Steam", id)).ToArray();
            if (admins.Length is < 1 or > 100 || string.IsNullOrWhiteSpace(name) || name.Length > 128
                || !ValidHost(c.PublicHost) || c.PublicPort is < 1 or > 65535)
            {
                SetOutbound(null);
                return;
            }
            var listing = new Listing("", name, c.PublicHost, c.PublicPort, now, true, null, null, null);
            if (Torch.Managers.GetManager<PluginManager>().Plugins.ContainsKey(NexusV3))
            {
                try
                {
                    nexus.Attach();
                    nexus.RequestTopology();
                    listing = listing with { Cluster = nexus.Capture() };
                }
                catch (Exception error)
                {
                    listing = listing with { Cluster = new("Nexus", null, null) };
                    Log.Warn("Nexus status unavailable ({0}).", error.GetType().Name);
                }
            }
            else
            {
                var players = new List<IMyPlayer>();
                MyAPIGateway.Players.GetPlayers(players, player => !player.IsBot);
                listing = listing with
                {
                    PlayersOnline = players.Count,
                    MaxPlayers = MySession.Static.MaxPlayers,
                    SimSpeed = Wire.Speed(MyAPIGateway.Physics.ServerSimulationRatio)
                };
            }
            SetOutbound(new(listing, admins));
        }
    }

    // Called under sync. Ordinary telemetry refreshes retain the regular heartbeat cadence.
    private void SetOutbound(Outbound? next)
    {
        var previous = outbound;
        outbound = next;
        if (previous is null ? next is null : next is not null && previous.Admins.SequenceEqual(next.Admins)) return;
        if (previous is not null)
        {
            transfers.Cancel();
            transfers.Dispose();
            transfers = new();
        }
        if (wake.CurrentCount == 0) wake.Release();
    }

    private static bool ValidHost(string? host) => host is { Length: > 0 and <= 253 }
        && Uri.CheckHostName(host) != UriHostNameType.Unknown && host != "localhost" && host is not ("0.0.0.0" or "::")
        && (!System.Net.IPAddress.TryParse(host, out var ip) || !System.Net.IPAddress.IsLoopback(ip));

    private async Task RunAsync()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri("https://se-servers.net/"), Timeout = TimeSpan.FromSeconds(15) };
        Publisher? publisher = null;
        var mayBeListed = false;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                Outbound? current;
                CancellationTokenSource cycle;
                lock (sync)
                {
                    current = config.Data.Enabled ? outbound : null;
                    if (current?.Listing.ObservedAtUtc < DateTimeOffset.UtcNow.AddSeconds(-45)) current = null;
                    cycle = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, transfers.Token);
                }
                using (cycle)
                {
                    try
                    {
                        if (current is not null || mayBeListed)
                        {
                            cycle.Token.ThrowIfCancellationRequested();
                            publisher ??= new Publisher(Path.Combine(StoragePath, "ServerListIdentity"), http);
                            mayBeListed = true;
                            var status = (int)await publisher.SendAsync(current?.Listing, current?.Admins ?? Array.Empty<Admin>(), cycle.Token);
                            if (status is >= 200 and < 300) mayBeListed = current is not null;
                            else Log.Warn("Server list update deferred (HTTP {0}).", status);
                        }
                    }
                    catch (OperationCanceledException) when (cycle.IsCancellationRequested) { }
                    catch (Exception error) { Log.Warn("Server list update deferred ({0}).", error.GetType().Name); }
                }
                await wake.WaitAsync(TimeSpan.FromSeconds(30), stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally { publisher?.Dispose(); }
    }

    public override void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            nexus.Dispose();
        }
        config.Data.PropertyChanged -= ConfigChanged;
        sessions.SessionStateChanged -= SessionChanged;
        stop.Cancel();
        worker?.GetAwaiter().GetResult();
        transfers.Dispose();
        stop.Dispose();
        wake.Dispose();
        base.Dispose();
    }
}
