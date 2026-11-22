using System.Net;
using System.Security.Cryptography;
using System.Text;
using CometWorks.ServerList.Torch;
using Newtonsoft.Json;
using ProtoBuf;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
// Nexus omits ServerID when it equals its declared protobuf default of 1.
using (var defaults = new MemoryStream(new byte[] { 0xa2, 1, 2, 0x18, 1, 0xf2, 1, 4, 0x18, 1, 0x68, 1, 0x90, 3, 1 }))
    Check(NexusTopology.Count(Serializer.Deserialize<NexusTopology>(defaults), id => id == 1) == new Cluster("Nexus", 1, 1),
        "decodes Nexus wire default when server ID is omitted");
var topology = new NexusTopology { CurrentCluster = 1, Servers = [new() { Id = 1, ClusterId = 1 },
    new() { Id = 2, ClusterId = 1 }, new() { Id = 3, ClusterId = 2 }],
    Sectors = [new() { Id = 1, ServerId = 1 }, new() { Id = 2, ServerId = 1 }, new() { Id = 3, ServerId = 2 },
        new() { Id = 4, ServerId = 3 }, new() { Id = 5, ServerId = 1, Hidden = true }] };
using (var bytes = new MemoryStream())
{
    Serializer.Serialize(bytes, topology); bytes.Position = 0;
    var received = Serializer.Deserialize<NexusTopology>(bytes);
    var counts = NexusTopology.Count(received, id => id == 1);
    Check(counts == new Cluster("Nexus", 2, 3), "counts sectors sharing a server; excludes other clusters and hidden sectors");
    Check(NexusTopology.Count(received, _ => false).Online == 0, "all sectors offline is a known zero");
    received.Sectors.Clear();
    Check(NexusTopology.Count(received, _ => true) == new Cluster("Nexus", 0, 0), "empty configured cluster is zero of zero");
}
Check(Wire.Speed(1.5f) == 1 && Wire.Speed(float.NaN) is null && Wire.Speed(-1) is null, "simulation speed rejects invalid readings and caps at one");
var root = Path.Combine(Path.GetTempPath(), "torch-list-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var handler = new DirectoryReceiver();
    using var http = new HttpClient(handler) { BaseAddress = new Uri("https://directory.example/") };
    var admins = new[] { new Admin("Steam", "76561198000000001") };
    var standalone = new Listing("", "Fixture community", "play.example.org", 27016, DateTimeOffset.UtcNow,
        true, 3, 16, 1);
    var nexus = standalone with { PlayersOnline = null, MaxPlayers = null, SimSpeed = null, Cluster = new("Nexus", 2, 3) };
    string installationId, listingId;
    using (var publisher = new Publisher(root, http))
    {
        installationId = publisher.InstallationId;
        listingId = publisher.ListingId;
        Check(await publisher.SendAsync(standalone, admins, default) == HttpStatusCode.NoContent,
            "fresh installation publishes directly with a key-derived identity and .NET-verifiable RSA-PSS signature");
        var first = handler.Accepted!;
        Check(first.Epoch == 1 && first.Sequence == 1 && first.Admins.SequenceEqual(admins)
            && first.Listings.Single() == standalone with { ListingId = listingId },
            "standalone publication carries Steam admins and Torch telemetry");
        Check(await publisher.SendAsync(nexus, admins, default) == HttpStatusCode.NoContent
            && handler.Accepted!.Sequence == 2 && handler.Accepted.Listings.Single() == nexus with { ListingId = listingId },
            "Nexus publication carries sector counts and leaves local player metrics unknown");
        Check(await publisher.SendAsync(null, admins, default) == HttpStatusCode.NoContent
            && handler.Accepted!.Sequence == 3 && handler.Accepted.Admins.Length == 0 && handler.Accepted.Listings.Length == 0,
            "withdrawal advances ordering with empty listings and admins");
        try { using var duplicate = new Publisher(root, http); Check(false, "duplicate publisher must be fenced"); }
        catch (IOException) { Check(true, "process lock prevents concurrent publishers sharing identity"); }
    }
    using (var restarted = new Publisher(root, http))
    {
        Check(restarted.InstallationId == installationId && restarted.ListingId == listingId,
            "restart retains machine and listing identity");
        Check(await restarted.SendAsync(standalone, admins, default) == HttpStatusCode.NoContent
            && handler.Accepted!.Epoch == 2 && handler.Accepted.Sequence == 1 && handler.Requests == 4,
            "restart reserves a higher epoch and sends one request per update");
    }

    var legacy = Path.Combine(root, "legacy");
    Directory.CreateDirectory(legacy);
    var oldInstallation = Guid.NewGuid().ToString("N");
    var oldListing = Guid.NewGuid().ToString("N");
    using var oldKey = RSA.Create(3072);
    File.WriteAllText(Path.Combine(legacy, "identity.json"), JsonConvert.SerializeObject(new[] { oldInstallation, oldListing }));
    File.WriteAllText(Path.Combine(legacy, "machine-key.pem"), oldKey.ExportRSAPrivateKeyPem());
    File.WriteAllText(Path.Combine(legacy, "epoch"), "7");

    var legacyReceiver = new DirectoryReceiver();
    using var legacyHttp = new HttpClient(legacyReceiver) { BaseAddress = http.BaseAddress };
    using var upgraded = new Publisher(legacy, legacyHttp);
    Check(await upgraded.SendAsync(nexus, admins, default) == HttpStatusCode.Forbidden,
        "an unimported legacy identity surfaces the directory rejection");
    legacyReceiver.Keys.Add(oldInstallation, DirectoryReceiver.Fingerprint(oldKey));
    Check(await upgraded.SendAsync(nexus, admins, default) == HttpStatusCode.NoContent
        && legacyReceiver.Accepted!.InstallationId == oldInstallation && legacyReceiver.Accepted.Epoch == 8
        && legacyReceiver.Accepted.Sequence == 2 && legacyReceiver.Accepted.Listings.Single().ListingId == oldListing,
        "imported legacy identity uses the original key, installation ID, listing ID and higher epoch");
}
finally { Directory.Delete(root, true); }

sealed class DirectoryReceiver : HttpMessageHandler
{
    public int Requests { get; private set; }
    public Publication? Accepted { get; private set; }
    public Dictionary<string, string> Keys { get; } = new();

    public static string Fingerprint(RSA key) => Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (request.Method != HttpMethod.Put || request.RequestUri?.AbsolutePath != "/v2/listings"
            || request.Headers.Authorization is not null || request.Headers.Contains("X-Installation-Id"))
            throw new Exception("Unexpected directory request");
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        { RespectRequiredConstructorParameters = true };
        var envelope = System.Text.Json.JsonSerializer.Deserialize<SignedPublication>(body, options)!;
        var spki = Convert.FromBase64String(envelope.PublicKey);
        var payload = Convert.FromBase64String(envelope.Payload);
        using var key = RSA.Create();
        key.ImportSubjectPublicKeyInfo(spki, out var read);
        byte[] signed = [.. Encoding.ASCII.GetBytes("CometWorks.Directory.Publication.v2\n"), .. payload];
        if (read != spki.Length || key.KeySize != 3072
            || !key.VerifyData(signed, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new Exception("Invalid publication signature");
        var publication = System.Text.Json.JsonSerializer.Deserialize<Publication>(payload, options)!;
        var now = DateTimeOffset.UtcNow;
        if (publication.SchemaVersion != 2 || publication.IssuedAtUtc <= now.AddSeconds(-30) || publication.IssuedAtUtc > now.AddSeconds(5)
            || publication.Epoch <= 0 || publication.Sequence <= 0)
            throw new Exception("Invalid publication schema, timestamp or ordering");
        var fingerprint = Fingerprint(key);
        if (Keys.TryGetValue(publication.InstallationId, out var pinned) ? pinned != fingerprint : publication.InstallationId != fingerprint[..32])
            return new(HttpStatusCode.Forbidden);
        if (Accepted is { } accepted && (publication.Epoch < accepted.Epoch
            || publication.Epoch == accepted.Epoch && publication.Sequence <= accepted.Sequence))
            return new(HttpStatusCode.Conflict);
        Keys[publication.InstallationId] = fingerprint;
        Accepted = publication;
        return new(HttpStatusCode.NoContent);
    }
}
