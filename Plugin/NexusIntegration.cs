using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ProtoBuf;
using Sandbox.ModAPI;
using VRage;
using VRage.Game.ModAPI;

namespace CometWorks.ServerList.Torch;

// Nexus V3's documented Mod API, channel 20240902. No Nexus DLL is packaged or required.
public sealed class NexusIntegration : IDisposable
{
    private const long Channel = 20240902;
    private IMyUtilities? utilities;
    private NexusTopology? topology;
    private Func<object, object>? isServerOnline;
    public bool Connected => topology is not null && isServerOnline is not null;

    public void Attach()
    {
        if (MyAPIGateway.Utilities is null || ReferenceEquals(utilities, MyAPIGateway.Utilities)) return;
        Dispose(); utilities = MyAPIGateway.Utilities;
        utilities.RegisterMessageHandler(Channel, Receive);
    }

    public void RequestTopology()
    {
        if (Connected || utilities is null) return;
        // The initial Nexus broadcast can precede this plugin's session callback.
        // Ask its public API to rebroadcast rather than reading private plugin state.
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("NGPlugin.API.NexusServerAPI")).FirstOrDefault(t => t is not null);
        var get = type?.BaseType?.GetMethods(BindingFlags.Static | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "GetSync" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        var api = get?.MakeGenericMethod(type!).Invoke(null, null);
        if (api is not null) type!.GetMethod("SendUpdatedData")?.Invoke(api, null);
    }

    private void Receive(object message)
    {
        if (message is not MyTuple<byte[], Func<int, Func<object, object>>> data) return;
        try
        {
            var received = utilities!.SerializeFromBinary<NexusTopology>(data.Item1);
            var online = data.Item2(4); // IsServerOnline in Nexus's documented method table.
            if (received is null || online is null) return;
            topology = received; isServerOnline = online;
        }
        catch (Exception error)
        {
            topology = null; isServerOnline = null;
            NLog.LogManager.GetCurrentClassLogger().Warn("Nexus API data unavailable ({0}).", error.GetType().Name);
        }
    }

    public Cluster Capture() => !Connected ? new("Nexus", null, null)
        : NexusTopology.Count(topology!, id => (bool)isServerOnline!(id));

    public void Dispose()
    {
        utilities?.UnregisterMessageHandler(Channel, Receive);
        utilities = null; topology = null; isServerOnline = null;
    }
}

