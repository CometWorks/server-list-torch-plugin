using System.Net;
using System.Security.Cryptography;
using System.Text;
using CometWorks.ServerList.Torch;
using Newtonsoft.Json;
using ProtoBuf;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
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
    var handler = new Backoffice();
    using var http = new HttpClient(handler) { BaseAddress = new Uri("https://backoffice.example/") };
    string identity, listingId;
    var listing = new Listing("", "Fixture community", "play.example.org", 27016, DateTimeOffset.UtcNow,
        true, null, null, null, Cluster: new("Nexus", 2, 3));
    using (var publisher = new Publisher(root, http))
    {
        identity = publisher.InstallationId; listingId = publisher.ListingId;
        await publisher.SendAsync(listing, [new("Steam", "76561198000000001")], default);
        Check(handler.Epoch == 1 && handler.Sequence == 1, "enrollment signature and OAEP decryption interoperate with .NET RSA");
        Check(handler.Publication?.Listings.Single().Cluster == listing.Cluster && handler.Publication!.Listings[0].ListingId == listingId,
            "publication carries Torch/Nexus telemetry with stable listing identity");
        await publisher.SendAsync(null, [], default);
        Check(handler.Sequence == 2 && handler.Withdrawn, "withdrawal advances source ordering");
        try { using var duplicate = new Publisher(root, http); Check(false, "duplicate publisher must be fenced"); }
        catch (IOException) { Check(true, "process lock prevents concurrent publishers sharing identity"); }
    }
    using (var restarted = new Publisher(root, http))
    {
        Check(restarted.InstallationId == identity && restarted.ListingId == listingId, "restart retains machine and listing identity");
        await restarted.SendAsync(listing, [new("Steam", "76561198000000001")], default);
        Check(handler.Epoch == 2 && handler.Sequence == 1 && handler.Enrollments == 2, "restart reserves higher epoch and re-enrolls same key");
    }
}
finally { Directory.Delete(root, true); }

sealed class Backoffice : HttpMessageHandler
{
    public long Epoch, Sequence;
    public int Enrollments;
    public bool Withdrawn;
    public Publication? Publication;
    private string? installation, publicKey;
    private const string Credential = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        if (request.RequestUri!.AbsolutePath == "/v1/installations/enroll")
        {
            var enrollment = JsonConvert.DeserializeObject<Enrollment>(body, Wire.Json)!;
            using var key = RSA.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(enrollment.PublicKey), out _);
            var signed = Encoding.UTF8.GetBytes($"CometWorks.Diagnostics.Enrollment.v1\n{enrollment.InstallationId}\n{enrollment.PublicKey}\n{enrollment.IssuedAt}");
            if (key.KeySize != 3072 || !key.VerifyData(signed, Convert.FromBase64String(enrollment.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)
                || installation is not null && (installation != enrollment.InstallationId || publicKey != enrollment.PublicKey))
                throw new Exception("Invalid enrollment signature/key binding");
            installation = enrollment.InstallationId; publicKey = enrollment.PublicKey; Enrollments++;
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(new EnrollmentResponse(installation,
                Convert.ToBase64String(key.Encrypt(Encoding.ASCII.GetBytes(Credential), RSAEncryptionPadding.OaepSHA256))), Wire.Json)) };
        }
        if (request.Headers.Authorization?.Parameter != Credential || request.Headers.GetValues("X-Installation-Id").Single() != installation)
            throw new Exception("Missing machine credential");
        Withdrawal order;
        if (request.RequestUri.AbsolutePath == "/v1/torch/publication" && request.Method == HttpMethod.Put)
        {
            Publication = JsonConvert.DeserializeObject<Publication>(body, Wire.Json)!;
            order = new(Publication.SchemaVersion, Publication.InstallationId, Publication.Epoch, Publication.Sequence); Withdrawn = false;
        }
        else if (request.RequestUri.AbsolutePath == "/v1/torch/withdraw" && request.Method == HttpMethod.Post)
        { order = JsonConvert.DeserializeObject<Withdrawal>(body, Wire.Json)!; Withdrawn = true; }
        else throw new Exception("Unexpected request path/method");
        if (order.InstallationId != installation || order.Epoch < Epoch || order.Epoch == Epoch && order.Sequence <= Sequence)
            throw new Exception("Stale or foreign source ordering");
        Epoch = order.Epoch; Sequence = order.Sequence;
        return new(HttpStatusCode.NoContent);
    }
}
