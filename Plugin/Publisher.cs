using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.Encoders;
using Org.BouncyCastle.X509;

namespace CometWorks.ServerList.Torch;

// Bouncy Castle supplies RSA-PSS and SPKI/PEM on .NET Framework 4.8.
public sealed class Publisher : IDisposable
{
    private static readonly byte[] SigningPrefix = Encoding.ASCII.GetBytes("CometWorks.Directory.Publication.v2\n");
    private readonly HttpClient http;
    private readonly FileStream processLock;
    private readonly AsymmetricCipherKeyPair key;
    private readonly string publicKey;
    private readonly long epoch;
    private long sequence;
    public string InstallationId { get; }
    public string ListingId { get; }
    private sealed record Identity(string InstallationId, string ListingId, string PrivateKeyPem);

    public Publisher(string directory, HttpClient http)
    {
        this.http = http;
        Directory.CreateDirectory(directory);
        processLock = new FileStream(Path.Combine(directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var identityPath = Path.Combine(directory, "identity.json");
            var legacyKeyPath = Path.Combine(directory, "machine-key.pem");
            var epochPath = Path.Combine(directory, "epoch");
            Identity identity;
            byte[] spki;
            if (File.Exists(identityPath))
            {
                var saved = JToken.Parse(File.ReadAllText(identityPath));
                // Legacy IDs stay bound to their existing key and directory page.
                identity = saved is JArray { Count: 2 } ids
                    ? new Identity((string?)ids[0] ?? "", (string?)ids[1] ?? "", File.ReadAllText(legacyKeyPath))
                    : saved.ToObject<Identity>() ?? throw new InvalidDataException("Invalid publisher identity.");
                if (!ValidId(identity.InstallationId) || !ValidId(identity.ListingId) || string.IsNullOrEmpty(identity.PrivateKeyPem))
                    throw new InvalidDataException("Invalid publisher identity.");
                using var reader = new StringReader(identity.PrivateKeyPem);
                key = new PemReader(reader).ReadObject() as AsymmetricCipherKeyPair
                    ?? throw new InvalidDataException("Invalid publisher key.");
                if (key.Public is not RsaKeyParameters rsa || rsa.Modulus.BitLength != 3072)
                    throw new InvalidDataException("Invalid publisher key.");
                spki = ExportPublicKey(key);
            }
            else
            {
                if (File.Exists(legacyKeyPath) || File.Exists(epochPath))
                    throw new InvalidDataException("Incomplete publisher identity; restore its matching identity file.");
                var generator = new RsaKeyPairGenerator();
                generator.Init(new KeyGenerationParameters(new SecureRandom(), 3072));
                key = generator.GenerateKeyPair();
                spki = ExportPublicKey(key);
                using var hash = SHA256.Create();
                var installationId = Hex.ToHexString(hash.ComputeHash(spki), 0, 16);
                using var writer = new StringWriter();
                new PemWriter(writer).WriteObject(key);
                identity = new Identity(installationId, Guid.NewGuid().ToString("N"), writer.ToString());
                DurableWrite(identityPath, JsonConvert.SerializeObject(identity));
            }
            InstallationId = identity.InstallationId;
            ListingId = identity.ListingId;
            publicKey = Convert.ToBase64String(spki);
            var previous = File.Exists(epochPath) ? long.Parse(File.ReadAllText(epochPath), CultureInfo.InvariantCulture) : 0;
            if (previous < 0) throw new InvalidDataException("Invalid publisher epoch.");
            epoch = checked(previous + 1);
            DurableWrite(epochPath, epoch.ToString(CultureInfo.InvariantCulture));
        }
        catch { processLock.Dispose(); throw; }
    }

    // The worker sends serially. Each attempt advances the sequence within a durable epoch.
    public async Task<HttpStatusCode> SendAsync(Listing? listing, Admin[] admins, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var publication = new Publication(2, InstallationId, epoch, checked(++sequence), DateTimeOffset.UtcNow,
            listing is null ? Array.Empty<Admin>() : admins,
            listing is null ? Array.Empty<Listing>() : new[] { listing with { ListingId = ListingId } });
        var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(publication, Wire.Json));
        var signer = new PssSigner(new RsaEngine(), new Sha256Digest(), 32);
        signer.Init(true, key.Private);
        signer.BlockUpdate(SigningPrefix, 0, SigningPrefix.Length);
        signer.BlockUpdate(payload, 0, payload.Length);
        var signed = new SignedPublication(publicKey, Convert.ToBase64String(payload), Convert.ToBase64String(signer.GenerateSignature()));
        using var request = new HttpRequestMessage(HttpMethod.Put, "v2/listings")
        {
            Content = new StringContent(JsonConvert.SerializeObject(signed, Wire.Json), Encoding.UTF8, "application/json")
        };
        token.ThrowIfCancellationRequested();
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        return response.StatusCode;
    }

    private static bool ValidId(string? id) => id is { Length: 32 }
        && Guid.TryParseExact(id, "N", out _) && id == id.ToLowerInvariant();

    private static byte[] ExportPublicKey(AsymmetricCipherKeyPair pair) =>
        SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(pair.Public).GetDerEncoded();

    private static void DurableWrite(string path, string value)
    {
        var temporary = path + ".tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            output.Write(bytes, 0, bytes.Length);
            output.Flush(true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    public void Dispose() => processLock.Dispose();
}
