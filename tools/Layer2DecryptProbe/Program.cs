using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assessment.Core.Utilities;
using Sodium;

var dataPath = @"C:\AA-PROJECT\src\Assessment.Api\data\dataset.bin";
var outPath = @"C:\AA-PROJECT\tools\Layer2DecryptProbe\output.txt";
var apiKey = "sa_90291610bea54baafd7c03676b33c31380315ea49c5289de4ed5955b27d0fe00";
var contentHash = "ca765b1e5464555b22a2ebd3e733c9f90b4f150ba7d89083cd0d2aaf8d2bf149";
var submissionId = "e6186978-c83d-4d98-9a5d-08fabdf8a425";

using var writer = new StreamWriter(outPath, false);
void Log(string line)
{
    Console.WriteLine(line);
    writer.WriteLine(line);
}

var json = await File.ReadAllTextAsync(dataPath);
using var doc = JsonDocument.Parse(json);
var samples = doc.RootElement.GetProperty("data").EnumerateArray().Take(3).Select(e => e.GetString()!).ToList();

Log("=== CIPHERTEXT STRUCTURE ===");
foreach (var (cipher, i) in samples.Select((c, i) => (c, i)))
{
    var bytes = Convert.FromBase64String(cipher);
    Log($"Sample {i}: b64Len={cipher.Length} decodedLen={bytes.Length} head32={Convert.ToHexString(bytes.AsSpan(0, Math.Min(32, bytes.Length)))}");
}

var keyCandidates = new List<(string Name, byte[] Key)>
{
    ("apiKeyHexBody", Convert.FromHexString(apiKey["sa_".Length..])),
    ("sha256FullApiKey", SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))),
    ("sha256ApiKeyBody", SHA256.HashData(Encoding.UTF8.GetBytes(apiKey["sa_".Length..]))),
    ("contentHashHex", Convert.FromHexString(contentHash)),
    ("sha256ContentHashHex", SHA256.HashData(Encoding.UTF8.GetBytes(contentHash))),
    ("submissionGuid", Guid.Parse(submissionId).ToByteArray()),
    ("hmacApiKeyBodyContentHash", HMACSHA256.HashData(Convert.FromHexString(apiKey["sa_".Length..]), Convert.FromHexString(contentHash))),
    ("hmacSha256ApiKeyContentHash", HMACSHA256.HashData(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)), Convert.FromHexString(contentHash))),
};

Log("");
Log("=== DECRYPTION ATTEMPTS ===");
foreach (var sample in samples)
{
    var cipherBytes = Convert.FromBase64String(sample);
    foreach (var (name, key) in keyCandidates)
    {
        if (TrySecretBox(cipherBytes, key, out var plain))
        {
            Log($"WIN secretbox key={name} plain={plain[..Math.Min(120, plain.Length)]}");
        }

        if (Layer2DecryptionEngine.TryDecryptToJsonObject(sample, ToKeyMaterial(name, key, apiKey, contentHash), out var jsonPlain))
        {
            Log($"WIN engine key={name} plain={jsonPlain[..Math.Min(120, jsonPlain.Length)]}");
        }
    }
}

Log("");
Log("=== API PROBES ===");
using var client = new HttpClient
{
    BaseAddress = new Uri("https://ca-seassessment-api-dev.happywater-190f264d.northcentralus.azurecontainerapps.io/")
};
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
foreach (var path in new[] { "api/v1/key", "api/v1/transcript", "api/v1/stats", "api/v1/dataset?batch=true&range=0-99" })
{
    try
    {
        using var resp = await client.GetAsync(path);
        var body = await resp.Content.ReadAsStringAsync();
        var snippet = body.Length > 300 ? body[..300] + "..." : body;
        Log($"GET {path} -> {(int)resp.StatusCode} body={snippet}");
        foreach (var h in resp.Headers.Concat(resp.Content.Headers))
        {
            if (h.Key.Contains("key", StringComparison.OrdinalIgnoreCase) ||
                h.Key.Equals("Link", StringComparison.OrdinalIgnoreCase) ||
                h.Key.Equals("ETag", StringComparison.OrdinalIgnoreCase) ||
                h.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
            {
                Log($"  {h.Key}: {string.Join(", ", h.Value)}");
            }
        }
    }
    catch (Exception ex)
    {
        Log($"GET {path} ERROR: {ex.Message}");
    }
}

Log("DONE");
await writer.FlushAsync();
return 0;

static string ToKeyMaterial(string name, byte[] key, string apiKey, string contentHash) => name switch
{
    "apiKeyHexBody" => apiKey["sa_".Length..],
    "contentHashHex" => contentHash,
    "sha256FullApiKey" or "sha256ApiKeyBody" or "submissionGuid" or "hmacApiKeyBodyContentHash" or "hmacSha256ApiKeyContentHash" or "sha256ContentHashHex" => Convert.ToHexString(key),
    _ => Convert.ToHexString(key)
};

static bool TrySecretBox(byte[] cipherWithNonce, byte[] key, out string plaintext)
{
    plaintext = string.Empty;
    const int nonceSize = 24;
    const int macSize = 16;
    if (cipherWithNonce.Length < nonceSize + macSize + 1)
    {
        return false;
    }

    try
    {
        var boxKey = key.Length switch
        {
            32 => key,
            _ => SHA256.HashData(key)
        };
        var nonce = cipherWithNonce.AsSpan(0, nonceSize).ToArray();
        var box = cipherWithNonce.AsSpan(nonceSize).ToArray();

        byte[] plain;
        try
        {
            plain = SecretBox.Open(box, nonce, boxKey);
        }
        catch
        {
            var mac = box.AsSpan(0, macSize).ToArray();
            var cipher = box.AsSpan(macSize).ToArray();
            plain = SecretBox.OpenDetached(cipher, mac, nonce, boxKey);
        }

        plaintext = Encoding.UTF8.GetString(plain);
        return plain.Length > 0 && (plaintext.TrimStart().StartsWith('{') || plaintext.TrimStart().StartsWith('['));
    }
    catch
    {
        return false;
    }
}
