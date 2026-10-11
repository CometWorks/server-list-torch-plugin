using System;
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
using Torch.API.Plugins;
using Torch.API.Managers;
using Torch.API.Session;
using Torch.Managers;
using Torch.Views;

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
    private long revision;
    private DateTimeOffset nextCapture;
    private ITorchSessionManager sessions = null!;
    private bool disposed;
    
    public UserControl GetControl() => new PropertyGrid { DataContext = config.Data };
    private sealed record Outbound(Listing Listing, Admin[] Admins, long Revision);

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
        if (state is TorchSessionState.Unloading or TorchSessionState.Unloaded) nexus.Dispose();
        lock (sync) { outbound = null; nextCapture = default; }
        Wake();
    }

    private void ConfigChanged(object sender, PropertyChangedEventArgs args)
    {
        lock (sync)
        {
            revision++; outbound = null; nextCapture = default;
            transfers.Cancel(); transfers.Dispose(); transfers = new();
            config.Save();
        }
        Wake();
    }

    public override void Update()
    {
        if (disposed || sessions.CurrentSession?.State != TorchSessionState.Loaded) return;
        nexus.Attach();
        lock (sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < nextCapture) return;
            nextCapture = now.AddSeconds(15);
            var c = config.Data;
            if (!c.Enabled) { outbound = null; return; }
            var name = string.IsNullOrWhiteSpace(c.Name) ? MySandboxGame.ConfigDedicated.ServerName : c.Name;
            var admins = MySandboxGame.ConfigDedicated.Administrators.Where(id => id.Length == 17 && id.All(char.IsDigit)
                && ulong.TryParse(id, out _)).Distinct().Select(id => new Admin("Steam", id)).ToArray();
            if (admins.Length is < 1 or > 100 || string.IsNullOrWhiteSpace(name) || name.Length > 128
                || !ValidHost(c.PublicHost) || c.PublicPort is < 1 or > 65535) { outbound = null; return; }
            var players = new System.Collections.Generic.List<VRage.Game.ModAPI.IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players, player => !player.IsBot);
            Cluster? cluster = null;
            if (Torch.Managers.GetManager<PluginManager>().Plugins.ContainsKey(NexusV3))
            {
                try { nexus.RequestTopology(); cluster = nexus.Capture(); }
                catch (Exception error) { cluster = new("Nexus", null, null); Log.Warn("Nexus status unavailable ({0}).", error.GetType().Name); }
            }
            var listing = new Listing("", name, c.PublicHost, c.PublicPort, now, true,
                players.Count, MySession.Static.MaxPlayers, Wire.Speed(MyAPIGateway.Physics.ServerSimulationRatio), Cluster: cluster);
            // Cluster-wide player totals are not inferred from this sector's local players.
            if (cluster is not null) listing = listing with { PlayersOnline = null, MaxPlayers = null, SimSpeed = null };
            if (outbound is { } old && !old.Admins.SequenceEqual(admins))
            { transfers.Cancel(); transfers.Dispose(); transfers = new(); Wake(); }
            outbound = new(listing, admins, revision);
        }
    }

    private static bool ValidHost(string? host) => host is { Length: > 0 and <= 253 }
        && Uri.CheckHostName(host) != UriHostNameType.Unknown && host != "localhost" && host is not ("0.0.0.0" or "::")
        && (!System.Net.IPAddress.TryParse(host, out var ip) || !System.Net.IPAddress.IsLoopback(ip));
    private void Wake() { try { wake.Release(); } catch (SemaphoreFullException) { } }

    private async Task RunAsync()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri("https://log.se-servers.net/"), Timeout = TimeSpan.FromSeconds(15) };
        Publisher? publisher = null;
        var attempted = false;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    Outbound? current;
                    CancellationTokenSource cycle;
                    lock (sync)
                    {
                        current = config.Data.Enabled && outbound?.Revision == revision ? outbound : null;
                        if (current?.Listing.ObservedAtUtc < DateTimeOffset.UtcNow.AddSeconds(-45)) current = null;
                        cycle = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, transfers.Token);
                    }
                    using (cycle)
                    {
                        if (current is not null || attempted)
                        {
                            publisher ??= new Publisher(Path.Combine(StoragePath, "ServerListIdentity"), http);
                            if (current is not null) attempted = true;
                            await publisher.SendAsync(current?.Listing, current?.Admins ?? Array.Empty<Admin>(), cycle.Token);
                            if (current is null) attempted = false;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { Log.Warn("Server list update deferred ({0}).", error.GetType().Name); }
                await wake.WaitAsync(TimeSpan.FromSeconds(30), stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally { publisher?.Dispose(); }
    }

    public override void Dispose()
    {
        disposed = true;
        config.Data.PropertyChanged -= ConfigChanged;
        sessions.SessionStateChanged -= SessionChanged;
        nexus.Dispose(); stop.Cancel();
        worker?.GetAwaiter().GetResult();
        transfers.Dispose(); stop.Dispose(); wake.Dispose();
        base.Dispose();
    }
}
