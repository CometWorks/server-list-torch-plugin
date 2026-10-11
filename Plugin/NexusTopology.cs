using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ProtoBuf;

namespace CometWorks.ServerList.Torch;

// Only the fields needed for public sector counts are decoded. Endpoints and names never leave Torch.
[ProtoContract]
public sealed class NexusTopology
{
    [ProtoMember(20)] public List<NexusServer> Servers { get; set; } = new();
    [ProtoMember(30)] public List<NexusSector> Sectors { get; set; } = new();
    [ProtoMember(50)] public byte CurrentCluster { get; set; }
    public static Cluster Count(NexusTopology topology, Func<byte, bool> online)
    {
        var servers = new HashSet<byte>(topology.Servers.Where(s => s.ClusterId == topology.CurrentCluster).Select(s => s.Id));
        var sectors = topology.Sectors.Where(s => !s.Hidden && servers.Contains(s.ServerId)).GroupBy(s => s.Id).Select(g => g.First()).ToArray();
        // Several sectors can share a server. Count sectors, not unique hosts or lobby servers.
        var states = sectors.Select(s => s.ServerId).Distinct().ToDictionary(id => id, online);
        return new("Nexus", sectors.Count(s => states[s.ServerId]), sectors.Length);
    }
}
[ProtoContract]
public sealed class NexusServer
{
    [ProtoMember(2), DefaultValue((byte)1)] public byte Id { get; set; } = 1;
    [ProtoMember(3)] public byte ClusterId { get; set; }
}
[ProtoContract]
public sealed class NexusSector
{
    [ProtoMember(3)] public byte ServerId { get; set; }
    [ProtoMember(13)] public ushort Id { get; set; }
    [ProtoMember(16)] public bool Hidden { get; set; }
}
