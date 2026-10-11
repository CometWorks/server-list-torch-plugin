using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace CometWorks.ServerList.Torch;

public sealed record Publication(int SchemaVersion, string InstallationId, long Epoch, long Sequence,
    long DiagnosticsGeneration, string ConsentDecisionUtc, string Challenge, Admin[] Admins, Listing[] Listings);
public sealed record Withdrawal(int SchemaVersion, string InstallationId, long Epoch, long Sequence);
public sealed record Admin(string Provider, string Subject);
public sealed record Listing(string ListingId, string Name, string PublicHost, int PublicPort,
    DateTimeOffset? ObservedAtUtc, bool? Running, int? PlayersOnline, int? MaxPlayers, float? SimSpeed,
    string Platform = "Torch", Cluster? Cluster = null);
public sealed record Cluster(string Kind, int? Online, int? Total);
public sealed record Enrollment(string InstallationId, string PublicKey, long IssuedAt, string Signature);
public sealed record EnrollmentResponse(string InstallationId, string EncryptedUploadToken);
public static class Wire
{
    public static readonly JsonSerializerSettings Json = new() { ContractResolver = new CamelCasePropertyNamesContractResolver() };
    public static float? Speed(float value) => float.IsNaN(value) || float.IsInfinity(value) || value < 0 ? null : Math.Min(value, 1f);
}
