using System.Text;
using System.Text.Json;

namespace Assessment.Core.Utilities;

public sealed record DatasetMergeResult(
    byte[] RawConcatenated,
    byte[] JsonArray,
    byte[] Ndjson,
    int RecordCount,
    int BatchCount);

public static class DatasetBatchMerger
{
    public static byte[] ConcatenateRaw(IReadOnlyList<byte[]> batches)
    {
        if (batches.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var total = batches.Sum(static b => b.Length);
        var output = new byte[total];
        var offset = 0;
        foreach (var batch in batches)
        {
            Buffer.BlockCopy(batch, 0, output, offset, batch.Length);
            offset += batch.Length;
        }

        return output;
    }

    public static DatasetMergeResult Merge(IReadOnlyList<byte[]> batches)
    {
        if (batches.Count == 0)
        {
            return new DatasetMergeResult(Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(), 0, 0);
        }

        var raw = ConcatenateRaw(batches);
        var envelopeStats = DatasetFormatParser.AnalyzeBatches(batches);
        if (envelopeStats.TotalCiphertextCount > 0)
        {
            return new DatasetMergeResult(
                raw,
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                envelopeStats.TotalCiphertextCount,
                envelopeStats.BatchCount);
        }

        var records = new List<JsonElement>();
        var parsedBatchCount = 0;

        foreach (var batch in batches)
        {
            var parsed = ParseLegacyBatch(batch);
            if (parsed.Count == 0)
            {
                continue;
            }

            parsedBatchCount++;
            records.AddRange(parsed);
        }

        return new DatasetMergeResult(
            raw,
            ToCompactJsonArray(records),
            ToNdjson(records),
            records.Count,
            parsedBatchCount);
    }

    private static IReadOnlyList<JsonElement> ParseLegacyBatch(byte[] batchBytes)
    {
        var text = Encoding.UTF8.GetString(batchBytes).Trim();
        if (text.Length == 0)
        {
            return Array.Empty<JsonElement>();
        }

        if (text.StartsWith('['))
        {
            return ParseJsonArray(text);
        }

        return ParseNdjson(text);
    }

    private static IReadOnlyList<JsonElement> ParseJsonArray(string text)
    {
        var records = new List<JsonElement>();
        if (text.Contains("]["))
        {
            foreach (var chunk in SplitConcatenatedArrays(text))
            {
                using var doc = JsonDocument.Parse(chunk);
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    records.Add(element.Clone());
                }
            }

            return records;
        }

        using (var doc = JsonDocument.Parse(text))
        {
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                records.Add(element.Clone());
            }
        }

        return records;
    }

    private static IEnumerable<string> SplitConcatenatedArrays(string text)
    {
        var parts = text.Split("][", StringSplitOptions.None);
        for (var i = 0; i < parts.Length; i++)
        {
            var chunk = parts[i];
            if (i == 0)
            {
                chunk += "]";
            }
            else if (i == parts.Length - 1)
            {
                chunk = "[" + chunk;
            }
            else
            {
                chunk = "[" + chunk + "]";
            }

            yield return chunk;
        }
    }

    private static IReadOnlyList<JsonElement> ParseNdjson(string text)
    {
        var records = new List<JsonElement>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var doc = JsonDocument.Parse(line);
            records.Add(doc.RootElement.Clone());
        }

        return records;
    }

    private static byte[] ToCompactJsonArray(IReadOnlyList<JsonElement> records)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartArray();
            foreach (var record in records)
            {
                record.WriteTo(writer);
            }

            writer.WriteEndArray();
        }

        return stream.ToArray();
    }

    private static byte[] ToNdjson(IReadOnlyList<JsonElement> records)
    {
        if (records.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var builder = new StringBuilder();
        for (var i = 0; i < records.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(records[i].GetRawText());
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}
