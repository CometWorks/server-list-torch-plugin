using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace CometWorks.ServerList.Torch;

// Bouncy Castle supplies portable RSA-PSS/OAEP-SHA256 and SPKI/PEM on .NET Framework 4.8.
public sealed class Publisher : IDisposable
{
    private readonly HttpClient http;
    private readonly FileStream processLock;
    private readonly AsymmetricCipherKeyPair key;
    private readonly long epoch;
    private long sequence;
    private string? credential;
    public string InstallationId { get; }
    public string ListingId { get; }

    public Publisher(string directory, HttpClient http)
    {
        this.http = http;
        Directory.CreateDirectory(directory);
        processLock = new FileStream(Path.Combine(directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var identity = Path.Combine(directory, "identity.json");
            var keyPath = Path.Combine(directory, "machine-key.pem");
            if (File.Exists(identity))
            {
                var ids = JsonConvert.DeserializeObject<string[]>(File.ReadAllText(identity))!;
                if (ids.Length != 2 || Array.Exists(ids, id => !Guid.TryParseExact(id, "N", out _) || id != id.ToLowerInvariant()))
                    throw new InvalidDataException("Invalid publisher identity.");
                InstallationId = ids[0]; ListingId = ids[1];
                using var reader = File.OpenText(keyPath);
                key = (AsymmetricCipherKeyPair)new PemReader(reader).ReadObject();
            }
            else
            {
                if (File.Exists(keyPath)) throw new InvalidDataException("Incomplete publisher identity; restore its matching identity file.");
                var generator = new RsaKeyPairGenerator();
                generator.Init(new KeyGenerationParameters(new SecureRandom(), 3072));
                key = generator.GenerateKeyPair();
                using var writer = new StringWriter();
                new PemWriter(writer).WriteObject(key);
                DurableWrite(keyPath, writer.ToString());
                InstallationId = Guid.NewGuid().ToString("N"); ListingId = Guid.NewGuid().ToString("N");
                DurableWrite(identity, JsonConvert.SerializeObject(new[] { InstallationId, ListingId }));
            }
            var epochPath = Path.Combine(directory, "epoch");
            var previous = File.Exists(epochPath) ? long.Parse(File.ReadAllText(epochPath), CultureInfo.InvariantCulture) : 0;
            if (previous < 0) throw new InvalidDataException("Invalid publisher epoch.");
            epoch = checked(previous + 1);
            DurableWrite(epochPath, epoch.ToString(CultureInfo.InvariantCulture));
        }
        catch { processLock.Dispose(); throw; }
    }

    public async Task SendAsync(Listing? listing, Admin[] admins, CancellationToken token)
    {
        var next = checked(++sequence); // One caller; restarts reserve a new durable epoch.
        if (credential is null)
        {
            var publicKey = Convert.ToBase64String(SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(key.Public).GetDerEncoded());
            var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var bytes = Encoding.UTF8.GetBytes($"CometWorks.Diagnostics.Enrollment.v1\n{InstallationId}\n{publicKey}\n{issued.ToString(CultureInfo.InvariantCulture)}");
            var signer = new PssSigner(new RsaEngine(), new Sha256Digest(), 32);
            signer.Init(true, key.Private); signer.BlockUpdate(bytes, 0, bytes.Length);
            using var enrollment = await SendJsonAsync(HttpMethod.Post, "v1/installations/enroll",
                new Enrollment(InstallationId, publicKey, issued, Convert.ToBase64String(signer.GenerateSignature())), token);
            var response = JsonConvert.DeserializeObject<EnrollmentResponse>(await ReadBoundedAsync(enrollment, token), Wire.Json)!;
            if (response.InstallationId != InstallationId) throw new InvalidDataException("Enrollment identity mismatch.");
            var decoder = new OaepEncoding(new RsaEngine(), new Sha256Digest());
            decoder.Init(false, key.Private);
            var encrypted = Convert.FromBase64String(response.EncryptedUploadToken);
            credential = Encoding.ASCII.GetString(decoder.ProcessBlock(encrypted, 0, encrypted.Length));
            if (credential.Length != 64) throw new InvalidDataException("Invalid enrollment credential.");
        }
        object payload = listing is null
            ? new Withdrawal(1, InstallationId, epoch, next)
            : new Publication(1, InstallationId, epoch, next, 1, DateTimeOffset.UtcNow.ToString("O"),
                new string('0', 32), admins, new[] { listing with { ListingId = ListingId } });
        using var result = await SendJsonAsync(listing is null ? HttpMethod.Post : HttpMethod.Put,
            listing is null ? "v1/torch/withdraw" : "v1/torch/publication", payload, token);
    }

    private async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string route, object payload, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, route);
        if (credential is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            request.Headers.Add("X-Installation-Id", InstallationId);
        }
        request.Content = new StringContent(JsonConvert.SerializeObject(payload, Wire.Json), Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) { var status = (int)response.StatusCode; response.Dispose(); throw new HttpRequestException("Backoffice status " + status); }
        return response;
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        using var stream = await response.Content.ReadAsStreamAsync();
        var bytes = new byte[8193]; int size = 0, read;
        while (size < bytes.Length && (read = await stream.ReadAsync(bytes, size, bytes.Length - size, token)) > 0) size += read;
        if (size > 8192) throw new InvalidDataException("Backoffice response exceeds limit.");
        return Encoding.UTF8.GetString(bytes, 0, size);
    }

    private static void DurableWrite(string path, string value)
    {
        var temporary = path + ".tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { var bytes = Encoding.UTF8.GetBytes(value); output.Write(bytes, 0, bytes.Length); output.Flush(true); }
        if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
    }
    public void Dispose() => processLock.Dispose();
}
