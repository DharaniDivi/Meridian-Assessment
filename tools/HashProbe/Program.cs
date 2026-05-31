using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var baseUrl = "https://ca-seassessment-api-dev.happywater-190f264d.northcentralus.azurecontainerapps.io";
var apiKey = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("ASSESSMENT_API_KEY") ?? "";
var dataPath = args.Length > 1 ? args[1] : @"C:\AA-PROJECT\src\Assessment.Api\data\dataset.bin";
var outPath = args.Length > 2 ? args[2] : @"C:\AA-PROJECT\tools\HashProbe\output.txt";

using var writer = new StreamWriter(outPath, false);
void Log(string line)
{
    Console.WriteLine(line);
    writer.WriteLine(line);
}

if (string.IsNullOrWhiteSpace(apiKey))
{
    Log("Missing API key");
    return 1;
}

using var client = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

Log("=== API PROBES ===");
await Probe(client, Log, "api/v1/dataset", true);
await Probe(client, Log, "api/v1/dataset?batch=true&range=0-99", true);
await Probe(client, Log, "api/v1/stats", false);

if (!File.Exists(dataPath))
{
    Log($"Missing dataset: {dataPath}");
    return 1;
}

var datasetBytes = await File.ReadAllBytesAsync(dataPath);
Log("");
Log("=== DATASET FILE ===");
Log($"Path: {dataPath}");
Log($"Size: {datasetBytes.Length}");
Log($"Raw SHA256: {Sha256(datasetBytes)}");

var batches = SplitJsonObjects(datasetBytes);
Log($"Envelope count: {batches.Count}");

var ciphertexts = new List<string>();
foreach (var batch in batches)
{
    if (batch.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    ciphertexts.Add(s);
                }
            }
        }
    }
}

Log($"Ciphertext count: {ciphertexts.Count}");

Log("");
Log("=== HASH CANDIDATES ===");
Log($"rawFile: {Sha256(datasetBytes)}");
Log($"etagKnown: 41154c43955ac405f9a8b4cecfb13b53e6aaf13657564fcc0c313db0d9ad7a02");

var batchRaw = batches.Select(b => Encoding.UTF8.GetBytes(b.GetRawText())).ToList();
Log($"rawConcatRebuilt: {Sha256(Concat(batchRaw))}");
Log($"ndjsonEnvelopes: {Sha256(Encoding.UTF8.GetBytes(string.Join('\n', batches.Select(b => b.GetRawText()))))}");
Log($"ndjsonEnvelopesTrailingNewline: {Sha256(Encoding.UTF8.GetBytes(string.Join('\n', batches.Select(b => b.GetRawText())) + '\n'))}");

using (var stream = new MemoryStream())
{
    using (var jsonWriter = new Utf8JsonWriter(stream))
    {
        jsonWriter.WriteStartArray();
        foreach (var c in ciphertexts)
        {
            jsonWriter.WriteStringValue(c);
        }
        jsonWriter.WriteEndArray();
    }

    Log($"ciphertextArrayJson: {Sha256(stream.ToArray())}");
}

Log($"ciphertextJoinedNoSep: {Sha256(Encoding.UTF8.GetBytes(string.Concat(ciphertexts)))}");
Log($"ciphertextNdjson: {Sha256(Encoding.UTF8.GetBytes(string.Join('\n', ciphertexts)))}");

using (var stream = new MemoryStream())
{
    using (var jsonWriter = new Utf8JsonWriter(stream))
    {
        jsonWriter.WriteStartObject();
        jsonWriter.WriteNumber("count", ciphertexts.Count);
        jsonWriter.WritePropertyName("data");
        jsonWriter.WriteStartArray();
        foreach (var c in ciphertexts)
        {
            jsonWriter.WriteStringValue(c);
        }
        jsonWriter.WriteEndArray();
        jsonWriter.WriteEndObject();
    }

    Log($"singleMergedEnvelope: {Sha256(stream.ToArray())}");
}

using (var stream = new MemoryStream())
{
    using (var jsonWriter = new Utf8JsonWriter(stream))
    {
        jsonWriter.WriteStartArray();
        foreach (var batch in batches)
        {
            batch.WriteTo(jsonWriter);
        }
        jsonWriter.WriteEndArray();
    }

    Log($"envelopeArrayJson: {Sha256(stream.ToArray())}");
}

// Hash each ciphertext bytes (base64 decoded) concatenated
var decoded = new MemoryStream();
foreach (var c in ciphertexts)
{
    decoded.Write(Convert.FromBase64String(c));
}
Log($"decodedCipherConcat: {Sha256(decoded.ToArray())}");

// First batch only (partial)
if (batches.Count > 0)
{
    var first = Encoding.UTF8.GetBytes(batches[0].GetRawText());
    Log($"firstBatchOnly: {Sha256(first)} size={first.Length}");
}

await writer.FlushAsync();
return 0;

static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static byte[] Concat(IReadOnlyList<byte[]> parts)
{
    var total = parts.Sum(p => p.Length);
    var output = new byte[total];
    var offset = 0;
    foreach (var part in parts)
    {
        Buffer.BlockCopy(part, 0, output, offset, part.Length);
        offset += part.Length;
    }

    return output;
}

static List<JsonElement> SplitJsonObjects(byte[] utf8Bytes)
{
    var results = new List<JsonElement>();
    var reader = new Utf8JsonReader(utf8Bytes, isFinalBlock: true, state: default);
    while (reader.Read())
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            continue;
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        results.Add(doc.RootElement.Clone());
    }

    return results;
}

static async Task Probe(HttpClient client, Action<string> log, string path, bool hashBody)
{
    try
    {
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsByteArrayAsync();
        log($"GET {path} -> {(int)response.StatusCode}, {body.Length} bytes");
        if (response.Headers.ETag is { } etag)
        {
            log($"  ETag: {etag.Tag}");
        }

        if (response.Headers.TryGetValues("Link", out var links))
        {
            log($"  Link: {string.Join(", ", links)}");
        }

        if (hashBody && body.Length > 0)
        {
            log($"  bodySha256: {Sha256(body)}");
            log($"  starts: {Encoding.UTF8.GetString(body.AsSpan(0, Math.Min(80, body.Length)))}");
        }
    }
    catch (Exception ex)
    {
        log($"GET {path} ERROR: {ex.Message}");
    }
}
