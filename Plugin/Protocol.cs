using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace CometWorks.ServerList.Torch;

public sealed record Publication(int SchemaVersion, string InstallationId, long Epoch, long Sequence,
    DateTimeOffset IssuedAtUtc, Admin[] Admins, Listing[] Listings);
public sealed record SignedPublication(string PublicKey, string Payload, string Signature);
public sealed record Admin(string Provider, string Subject);
public sealed record Listing(string ListingId, string Name, string PublicHost, int PublicPort,
    DateTimeOffset? ObservedAtUtc, bool? Running, int? PlayersOnline, int? MaxPlayers, float? SimSpeed,
    string Platform = "Torch", Cluster? Cluster = null);
public sealed record Cluster(string Kind, int? Online, int? Total);
public static class Wire
{
    public static readonly JsonSerializerSettings Json = new() { ContractResolver = new CamelCasePropertyNamesContractResolver() };
    public static float? Speed(float value) => float.IsNaN(value) || float.IsInfinity(value) || value < 0 ? null : Math.Min(value, 1f);
}
