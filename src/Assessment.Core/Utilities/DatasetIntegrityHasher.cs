using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Assessment.Core.Utilities;

public sealed record DatasetIntegrityHashes(
    string PrimaryHash,
    string PrimaryFormat,
    int CiphertextCount,
    IReadOnlyDictionary<string, string> All);

public static class DatasetIntegrityHasher
{
    private static readonly JsonWriterOptions RelaxedJsonOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static DatasetIntegrityHashes Compute(
        IReadOnlyList<byte[]> batches,
        byte[]? indexBytes = null)
    {
        var rawConcat = DatasetBatchMerger.ConcatenateRaw(batches);
        var ciphertexts = ExtractCiphertexts(batches);
        var rawLiterals = ExtractCiphertextRawLiterals(batches);
        var batchEnvelopeRawTexts = ExtractBatchEnvelopeRawTexts(batches);
        var dataOnlyEnvelope = BuildDataOnlyEnvelopeFromRawLiterals(rawLiterals);
        var dataOnlyRelaxed = BuildDataOnlyEnvelopeRelaxed(ciphertexts);
        var ndjsonBatches = BuildNdjsonBatches(batches);
        var ndjsonBatchesTrailingNewline = AppendNewline(ndjsonBatches);
        var mergedEnvelope = BuildMergedEnvelopeRelaxed(ciphertexts);
        var ciphertextArray = BuildCiphertextArrayRelaxed(ciphertexts);
        var ciphertextArrayStrict = BuildCiphertextArrayStrict(ciphertexts);
        var ciphertextNdjson = BuildCiphertextNdjson(ciphertexts);
        var ciphertextJoinedNoSep = BuildCiphertextJoinedNoSep(ciphertexts);
        var envelopeArrayJson = BuildEnvelopeArrayJson(batchEnvelopeRawTexts);
        var decodedCipherConcat = BuildDecodedCipherConcat(ciphertexts);
        var indexPlusBatches = BuildIndexPlusBatches(indexBytes, batches);

        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rawConcat"] = ContentHasher.Sha256Hex(rawConcat),
            ["ndjsonBatches"] = ContentHasher.Sha256Hex(ndjsonBatches),
            ["ndjsonBatchesTrailingNewline"] = ContentHasher.Sha256Hex(ndjsonBatchesTrailingNewline),
            ["mergedEnvelope"] = ContentHasher.Sha256Hex(mergedEnvelope),
            ["dataOnlyEnvelope"] = ContentHasher.Sha256Hex(dataOnlyEnvelope),
            ["dataOnlyRelaxed"] = ContentHasher.Sha256Hex(dataOnlyRelaxed),
            ["ciphertextArray"] = ContentHasher.Sha256Hex(ciphertextArray),
            ["ciphertextArrayStrict"] = ContentHasher.Sha256Hex(ciphertextArrayStrict),
            ["ciphertextNdjson"] = ContentHasher.Sha256Hex(ciphertextNdjson),
            ["ciphertextJoinedNoSep"] = ContentHasher.Sha256Hex(ciphertextJoinedNoSep),
            ["envelopeArrayJson"] = ContentHasher.Sha256Hex(envelopeArrayJson),
            ["decodedCipherConcat"] = ContentHasher.Sha256Hex(decodedCipherConcat)
        };

        if (indexPlusBatches.Length > 0)
        {
            all["indexPlusBatches"] = ContentHasher.Sha256Hex(indexPlusBatches);
        }

        if (batches.Count > 1)
        {
            var tail = DatasetBatchMerger.ConcatenateRaw(batches.Skip(1).ToList());
            all["batchesTailConcat"] = ContentHasher.Sha256Hex(tail);
            if (indexBytes is { Length: > 0 })
            {
                all["indexPlusBatchesTail"] = ContentHasher.Sha256Hex(
                    BuildIndexPlusBatches(indexBytes, batches.Skip(1).ToList()));
            }
        }

        if (batches.Count > 0)
        {
            all["firstBatchOnly"] = ContentHasher.Sha256Hex(batches[0]);
        }

        if (indexBytes is { Length: > 0 })
        {
            all["indexBody"] = ContentHasher.Sha256Hex(indexBytes);
            if (TryParseIndexCiphertextCount(indexBytes, out var indexCount))
            {
                all["indexCiphertextCount"] = indexCount.ToString();
            }

            if (batches.Count > 0 && indexBytes.AsSpan().SequenceEqual(batches[0]))
            {
                all["indexMatchesFirstBatch"] = "true";
            }
        }

        var primaryFormat = ciphertexts.Count > 0 ? "decodedCipherConcat" : "rawConcat";
        var primaryHash = all[primaryFormat];

        return new DatasetIntegrityHashes(primaryHash, primaryFormat, rawLiterals.Count, all);
    }

    public static byte[] BuildCanonicalDatasetBytes(IReadOnlyList<byte[]> batches)
    {
        var rawLiterals = ExtractCiphertextRawLiterals(batches);
        if (rawLiterals.Count > 0)
        {
            return BuildDataOnlyEnvelopeFromRawLiterals(rawLiterals);
        }

        var ciphertexts = ExtractCiphertexts(batches);
        return ciphertexts.Count > 0
            ? BuildDataOnlyEnvelopeRelaxed(ciphertexts)
            : DatasetBatchMerger.ConcatenateRaw(batches);
    }

    public static IReadOnlyList<string> ExtractCiphertexts(IReadOnlyList<byte[]> batches)
    {
        var ciphertexts = new List<string>();
        foreach (var batch in batches)
        {
            foreach (var envelope in DatasetFormatParser.ParseJsonObjects(batch))
            {
                if (!envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var value = item.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            ciphertexts.Add(value);
                        }
                    }
                }
            }
        }

        return ciphertexts;
    }

    public static IReadOnlyList<string> ExtractCiphertextRawLiterals(IReadOnlyList<byte[]> batches)
    {
        var literals = new List<string>();
        foreach (var batch in batches)
        {
            foreach (var envelope in DatasetFormatParser.ParseJsonObjects(batch))
            {
                if (!envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var raw = item.GetRawText();
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            literals.Add(raw);
                        }
                    }
                }
            }
        }

        return literals;
    }

    public static IReadOnlyList<string> ExtractBatchEnvelopeRawTexts(IReadOnlyList<byte[]> batches)
    {
        var texts = new List<string>();
        foreach (var batch in batches)
        {
            foreach (var envelope in DatasetFormatParser.ParseJsonObjects(batch))
            {
                texts.Add(envelope.GetRawText());
            }
        }

        return texts;
    }

    private static byte[] AppendNewline(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return bytes;
        }

        var output = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, output, 0, bytes.Length);
        output[^1] = (byte)'\n';
        return output;
    }

    private static byte[] BuildCiphertextArrayStrict(IReadOnlyList<string> ciphertexts)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var ciphertext in ciphertexts)
            {
                writer.WriteStringValue(ciphertext);
            }

            writer.WriteEndArray();
        }

        return stream.ToArray();
    }

    private static byte[] BuildCiphertextJoinedNoSep(IReadOnlyList<string> ciphertexts)
        => Encoding.UTF8.GetBytes(string.Concat(ciphertexts));

    private static byte[] BuildEnvelopeArrayJson(IReadOnlyList<string> batchEnvelopeRawTexts)
    {
        if (batchEnvelopeRawTexts.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var builder = new StringBuilder(batchEnvelopeRawTexts.Count * 512);
        builder.Append('[');
        for (var i = 0; i < batchEnvelopeRawTexts.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(batchEnvelopeRawTexts[i]);
        }

        builder.Append(']');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static byte[] BuildDecodedCipherConcat(IReadOnlyList<string> ciphertexts)
    {
        using var stream = new MemoryStream();
        foreach (var ciphertext in ciphertexts)
        {
            var decoded = Convert.FromBase64String(ciphertext);
            stream.Write(decoded, 0, decoded.Length);
        }

        return stream.ToArray();
    }

    private static byte[] BuildIndexPlusBatches(byte[]? indexBytes, IReadOnlyList<byte[]> batches)
    {
        if (indexBytes is not { Length: > 0 } || batches.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var batchBytes = DatasetBatchMerger.ConcatenateRaw(batches);
        var output = new byte[indexBytes.Length + batchBytes.Length];
        Buffer.BlockCopy(indexBytes, 0, output, 0, indexBytes.Length);
        Buffer.BlockCopy(batchBytes, 0, output, indexBytes.Length, batchBytes.Length);
        return output;
    }

    public static byte[] BuildBatchDigestBinaryConcat(IReadOnlyList<string> batchEtags)
    {
        using var stream = new MemoryStream(batchEtags.Count * 32);
        foreach (var etag in batchEtags)
        {
            stream.Write(Convert.FromHexString(etag));
        }

        return stream.ToArray();
    }

    private static byte[] BuildDataOnlyEnvelopeFromRawLiterals(IReadOnlyList<string> rawLiterals)
    {
        if (rawLiterals.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var builder = new StringBuilder(rawLiterals.Count * 256);
        builder.Append("{\"data\":[");
        for (var i = 0; i < rawLiterals.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(rawLiterals[i]);
        }

        builder.Append("]}");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static byte[] BuildDataOnlyEnvelopeRelaxed(IReadOnlyList<string> ciphertexts)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, RelaxedJsonOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("data");
            writer.WriteStartArray();
            foreach (var ciphertext in ciphertexts)
            {
                writer.WriteStringValue(ciphertext);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static byte[] BuildMergedEnvelopeRelaxed(IReadOnlyList<string> ciphertexts)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, RelaxedJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("count", ciphertexts.Count);
            writer.WritePropertyName("data");
            writer.WriteStartArray();
            foreach (var ciphertext in ciphertexts)
            {
                writer.WriteStringValue(ciphertext);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static byte[] BuildNdjsonBatches(IReadOnlyList<byte[]> batches)
    {
        if (batches.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var parts = batches.Select(b => Encoding.UTF8.GetString(b).Trim()).Where(p => p.Length > 0);
        return Encoding.UTF8.GetBytes(string.Join('\n', parts));
    }

    private static byte[] BuildCiphertextArrayRelaxed(IReadOnlyList<string> ciphertexts)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, RelaxedJsonOptions))
        {
            writer.WriteStartArray();
            foreach (var ciphertext in ciphertexts)
            {
                writer.WriteStringValue(ciphertext);
            }

            writer.WriteEndArray();
        }

        return stream.ToArray();
    }

    private static byte[] BuildCiphertextNdjson(IReadOnlyList<string> ciphertexts)
        => Encoding.UTF8.GetBytes(string.Join('\n', ciphertexts));

    private static bool TryParseIndexCiphertextCount(byte[] indexBytes, out int count)
    {
        count = 0;
        try
        {
            using var doc = JsonDocument.Parse(indexBytes);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
            {
                count = data.GetArrayLength();
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }
}
