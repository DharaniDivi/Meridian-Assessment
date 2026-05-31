using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assessment.Core.Configuration;
using Assessment.Core.Http;
using Assessment.Core.Models;
using Assessment.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Assessment.Core.Services;

public sealed class Layer2Service
{
    private static readonly string[] EncryptedFieldNames =
    [
        "ciphertext", "payload", "data", "encrypted", "content", "value", "body", "record"
    ];

    private readonly KeyAcquisitionService _keys;
    private readonly AssessmentOptions _options;
    private readonly ILogger<Layer2Service> _logger;

    public Layer2Service(
        KeyAcquisitionService keys,
        IOptions<AssessmentOptions> options,
        ILogger<Layer2Service> logger)
    {
        _keys = keys;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Layer2Result> DecryptDatasetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var encryptedBytes = await ResolveDatasetBytesAsync(cancellationToken);
            if (encryptedBytes.Length == 0)
            {
                return new Layer2Result(false, 0, null, "Dataset not found. Run Layer 1 first.");
            }

            var ciphertexts = DatasetFormatParser.ExtractCiphertexts(encryptedBytes);
            if (ciphertexts.Count == 0)
            {
                return new Layer2Result(false, 0, null, "No ciphertexts found in cached dataset.");
            }

            var (keyMaterial, source) = await ResolveDecryptionKeyAsync(ciphertexts, cancellationToken);
            if (string.IsNullOrWhiteSpace(keyMaterial))
            {
                return new Layer2Result(
                    false,
                    0,
                    null,
                    "No working decryption key found. Layer 1 passed — key may be derived from content_hash or API key.");
            }

            await _keys.SaveKeyAsync(keyMaterial, cancellationToken);
            _logger.LogInformation("Layer 2: using key from {Source}", source);

            var records = DecryptCiphertextList(ciphertexts, keyMaterial);

            if (records.Count == 0 || records.Count(r => !r.RawJson.Contains("decrypt_failed")) == 0)
            {
                return new Layer2Result(false, 0, null, "No records decrypted. Check key and dataset format.");
            }

            var outputPath = Path.Combine(_options.DataDirectory, "decrypted.jsonl");
            await File.WriteAllLinesAsync(
                outputPath,
                records.Select(r => r.RawJson),
                cancellationToken);

            var hashHex = await ComputeDecryptedHashAsync(cancellationToken);
            _logger.LogInformation(
                "Layer 2: decrypted {Count} records, SHA-256={Hash}",
                records.Count,
                hashHex);

            return new Layer2Result(true, records.Count, hashHex, null);
        }
        catch (AssessmentApiException ex)
        {
            _logger.LogError(ex, "Layer 2 API error {StatusCode}", ex.StatusCode);
            return new Layer2Result(false, 0, null, string.IsNullOrWhiteSpace(ex.Message) ? $"HTTP {ex.StatusCode}" : ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Layer 2 failed");
            return new Layer2Result(false, 0, null, ex.Message);
        }
    }

    public async Task<string?> ComputeDecryptedHashAsync(CancellationToken cancellationToken = default)
    {
        var outputPath = Path.Combine(_options.DataDirectory, "decrypted.jsonl");
        if (!File.Exists(outputPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(outputPath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<IReadOnlyList<DecryptedRecord>> LoadDecryptedRecordsAsync(CancellationToken cancellationToken = default)
    {
        var outputPath = Path.Combine(_options.DataDirectory, "decrypted.jsonl");
        if (!File.Exists(outputPath))
        {
            return Array.Empty<DecryptedRecord>();
        }

        var lines = await File.ReadAllLinesAsync(outputPath, cancellationToken);
        return ParseRecords(string.Join('\n', lines));
    }

    public async Task<object> TryKeyCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var encryptedBytes = await ResolveDatasetBytesAsync(cancellationToken);
        var ciphertexts = DatasetFormatParser.ExtractCiphertexts(encryptedBytes);
        var derived = await _keys.GetDerivedKeyCandidatesAsync(cancellationToken);
        var results = derived
            .Select(d => new { d.Source, works = TryKeyAgainstSample(ciphertexts, d.Key) })
            .ToList();

        string? samplePreview = null;
        if (ciphertexts.Count > 0)
        {
            var bytes = Convert.FromBase64String(ciphertexts[0]);
            samplePreview = Convert.ToHexString(bytes.AsSpan(0, Math.Min(32, bytes.Length)));
        }

        return new
        {
            sampleSize = Math.Min(3, ciphertexts.Count),
            sampleDecodedLength = ciphertexts.Count > 0 ? Convert.FromBase64String(ciphertexts[0]).Length : 0,
            sampleHeadHex = samplePreview,
            results
        };
    }

    private async Task<byte[]> ResolveDatasetBytesAsync(CancellationToken cancellationToken)
    {
        var datasetPath = Path.Combine(_options.DataDirectory, "dataset.bin");
        if (File.Exists(datasetPath))
        {
            return await File.ReadAllBytesAsync(datasetPath, cancellationToken);
        }

        var batchesPath = Path.Combine(_options.DataDirectory, "dataset-batches.raw");
        if (File.Exists(batchesPath))
        {
            return await File.ReadAllBytesAsync(batchesPath, cancellationToken);
        }

        return Array.Empty<byte>();
    }

    private async Task<(string? Key, string? Source)> ResolveDecryptionKeyAsync(
        IReadOnlyList<string> ciphertexts,
        CancellationToken cancellationToken)
    {
        var (primaryKey, primarySource, _) = await _keys.AcquireKeyAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(primaryKey) && ValidatesAgainstSample(ciphertexts, primaryKey))
        {
            return (primaryKey, primarySource);
        }

        foreach (var (candidate, source) in await _keys.GetDerivedKeyCandidatesAsync(cancellationToken))
        {
            if (ValidatesAgainstSample(ciphertexts, candidate))
            {
                _logger.LogInformation("Layer 2: validated decryption key candidate from {Source}", source);
                return (candidate, source);
            }
        }

        return (null, null);
    }

    private static bool ValidatesAgainstSample(IReadOnlyList<string> ciphertexts, string keyMaterial)
        => TryKeyAgainstSample(ciphertexts, keyMaterial);

    internal static bool TryKeyAgainstSample(IReadOnlyList<string> ciphertexts, string keyMaterial)
    {
        var sampleSize = Math.Min(3, ciphertexts.Count);
        var successes = 0;
        for (var i = 0; i < sampleSize; i++)
        {
            if (Layer2DecryptionEngine.TryDecryptToJsonObject(ciphertexts[i], keyMaterial, out _))
            {
                successes++;
            }
        }

        return successes == sampleSize && sampleSize > 0;
    }

    private static bool TryDecryptToJsonObject(string ciphertext, string keyMaterial, out string plaintext)
        => Layer2DecryptionEngine.TryDecryptToJsonObject(ciphertext, keyMaterial, out plaintext);

    private static List<DecryptedRecord> DecryptCiphertextList(IReadOnlyList<string> ciphertexts, string keyMaterial)
    {
        var records = new List<DecryptedRecord>();
        for (var i = 0; i < ciphertexts.Count; i++)
        {
            try
            {
                var decryptedPayload = DecryptCipherText(ciphertexts[i], keyMaterial);
                using var inner = JsonDocument.Parse(decryptedPayload);
                var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                if (inner.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in inner.RootElement.EnumerateObject())
                    {
                        fields[property.Name] = ElementToObject(property.Value);
                    }
                }

                records.Add(new DecryptedRecord(i, inner.RootElement.GetRawText(), fields));
            }
            catch
            {
                records.Add(new DecryptedRecord(
                    i,
                    JsonSerializer.Serialize(new { error = "decrypt_failed" }),
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)));
            }
        }

        return records;
    }

    internal static List<DecryptedRecord> DecryptBatchEnvelopeDataset(byte[] datasetBytes, string keyMaterial)
    {
        var records = new List<DecryptedRecord>();
        var index = 0;
        foreach (var ciphertext in DatasetFormatParser.ExtractCiphertexts(datasetBytes))
        {
            try
            {
                var decryptedPayload = DecryptCipherText(ciphertext, keyMaterial);
                using var inner = JsonDocument.Parse(decryptedPayload);
                var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                if (inner.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in inner.RootElement.EnumerateObject())
                    {
                        fields[property.Name] = ElementToObject(property.Value);
                    }
                }

                records.Add(new DecryptedRecord(index, inner.RootElement.GetRawText(), fields));
            }
            catch
            {
                records.Add(new DecryptedRecord(
                    index,
                    JsonSerializer.Serialize(new { ciphertext, error = "decrypt_failed" }),
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)));
            }

            index++;
        }

        return records;
    }

    internal static List<DecryptedRecord> DecryptJsonDataset(string jsonText, string keyMaterial)
    {
        var trimmed = jsonText.Trim();
        var records = new List<DecryptedRecord>();

        if (trimmed.StartsWith('['))
        {
            using var doc = JsonDocument.Parse(trimmed);
            var index = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                records.Add(DecryptRecordElement(index++, element, keyMaterial));
            }

            return records;
        }

        var lineIndex = 0;
        foreach (var line in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var doc = JsonDocument.Parse(line);
            records.Add(DecryptRecordElement(lineIndex++, doc.RootElement, keyMaterial));
        }

        return records;
    }

    internal static DecryptedRecord DecryptRecordElement(int index, JsonElement element, string keyMaterial)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new DecryptedRecord(index, element.GetRawText(), new Dictionary<string, object?>());
        }

        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        string? decryptedPayload = null;

        foreach (var property in element.EnumerateObject())
        {
            if (decryptedPayload is null && EncryptedFieldNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                var cipherText = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(cipherText))
                {
                    try
                    {
                        decryptedPayload = DecryptCipherText(cipherText, keyMaterial);
                    }
                    catch
                    {
                        // keep trying other fields
                    }
                }
            }

            fields[property.Name] = ElementToObject(property.Value);
        }

        if (!string.IsNullOrWhiteSpace(decryptedPayload))
        {
            try
            {
                using var inner = JsonDocument.Parse(decryptedPayload);
                if (inner.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in inner.RootElement.EnumerateObject())
                    {
                        fields[property.Name] = ElementToObject(property.Value);
                    }
                }

                return new DecryptedRecord(index, inner.RootElement.GetRawText(), fields);
            }
            catch
            {
                fields["_decrypted"] = decryptedPayload;
                return new DecryptedRecord(index, JsonSerializer.Serialize(fields), fields);
            }
        }

        return new DecryptedRecord(index, element.GetRawText(), fields);
    }

    internal static string DecryptCipherText(string cipherText, string keyMaterial)
    {
        if (Layer2DecryptionEngine.TryDecryptToJsonObject(cipherText, keyMaterial, out var plaintext))
        {
            return plaintext;
        }

        throw new InvalidOperationException("Unable to decrypt record ciphertext.");
    }

    internal static string DecryptPayload(byte[] encryptedBytes, string keyMaterial)
    {
        if (encryptedBytes.Length > 0 && encryptedBytes[0] is (byte)'{' or (byte)'[')
        {
            return Encoding.UTF8.GetString(encryptedBytes);
        }

        var asText = Encoding.UTF8.GetString(encryptedBytes).Trim();
        if (asText.StartsWith('{') || asText.StartsWith('['))
        {
            return asText;
        }

        byte[] cipherBytes;
        try
        {
            cipherBytes = Convert.FromBase64String(asText);
        }
        catch
        {
            cipherBytes = encryptedBytes;
        }

        var key = DeriveKey(keyMaterial);
        if (TryAesGcmDecrypt(cipherBytes, key, out var plaintext) ||
            TryAesCbcDecrypt(cipherBytes, key, out plaintext))
        {
            return Encoding.UTF8.GetString(plaintext);
        }

        throw new InvalidOperationException("Unable to decrypt dataset blob.");
    }

    internal static List<DecryptedRecord> ParseRecords(string decryptedText)
    {
        var records = new List<DecryptedRecord>();
        var trimmed = decryptedText.Trim();

        if (trimmed.StartsWith('['))
        {
            using var doc = JsonDocument.Parse(trimmed);
            var index = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                records.Add(ToRecord(index++, element.GetRawText(), element));
            }

            return records;
        }

        var lineIndex = 0;
        foreach (var line in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var doc = JsonDocument.Parse(line);
            records.Add(ToRecord(lineIndex++, line, doc.RootElement));
        }

        return records;
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static object? ElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText()
    };

    private static DecryptedRecord ToRecord(int index, string rawJson, JsonElement element)
    {
        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                fields[property.Name] = ElementToObject(property.Value);
            }
        }

        return new DecryptedRecord(index, rawJson, fields);
    }

    private static byte[] DeriveKey(string keyMaterial)
    {
        var trimmed = keyMaterial.Trim();
        if (IsHex(trimmed) && trimmed.Length is 64 or 32)
        {
            return Convert.FromHexString(trimmed.Length == 64 ? trimmed : trimmed.PadRight(64, '0'));
        }

        try
        {
            var decoded = Convert.FromBase64String(trimmed);
            if (decoded.Length is 16 or 24 or 32)
            {
                return decoded;
            }
        }
        catch
        {
            // fall through
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
    }

    private static bool TryAesGcmDecrypt(byte[] cipherBytes, byte[] key, out byte[] plaintext)
    {
        plaintext = Array.Empty<byte>();
        if (cipherBytes.Length < 28)
        {
            return false;
        }

        try
        {
            var nonce = cipherBytes.AsSpan(0, 12);
            var tag = cipherBytes.AsSpan(cipherBytes.Length - 16, 16);
            var cipher = cipherBytes.AsSpan(12, cipherBytes.Length - 28);
            plaintext = new byte[cipher.Length];
            using var aes = new AesGcm(NormalizeAesKey(key), 16);
            aes.Decrypt(nonce, cipher, tag, plaintext);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryAesCbcDecrypt(byte[] cipherBytes, byte[] key, out byte[] plaintext)
    {
        plaintext = Array.Empty<byte>();
        if (cipherBytes.Length < 32 || cipherBytes.Length % 16 != 0)
        {
            return false;
        }

        try
        {
            var iv = cipherBytes.AsSpan(0, 16);
            var cipher = cipherBytes.AsSpan(16);
            using var aes = Aes.Create();
            aes.Key = NormalizeAesKey(key);
            aes.IV = iv.ToArray();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            plaintext = decryptor.TransformFinalBlock(cipher.ToArray(), 0, cipher.Length);
            return plaintext.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] NormalizeAesKey(byte[] key) => key.Length switch
    {
        16 or 24 or 32 => key,
        _ => SHA256.HashData(key)
    };

    private static bool IsHex(string value)
        => value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}
