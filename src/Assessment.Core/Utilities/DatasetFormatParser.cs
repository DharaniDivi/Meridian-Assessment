using System.Text;
using System.Text.Json;

namespace Assessment.Core.Utilities;

public sealed record DatasetEnvelopeStats(
    int BatchCount,
    int TotalCiphertextCount,
    IReadOnlyList<int> CountsPerBatch);

public static class DatasetFormatParser
{
    public static DatasetEnvelopeStats AnalyzeBatches(IReadOnlyList<byte[]> batches)
    {
        var counts = new List<int>();
        foreach (var batch in batches)
        {
            var count = CountCiphertextsInBatch(batch);
            if (count > 0)
            {
                counts.Add(count);
            }
        }

        return new DatasetEnvelopeStats(counts.Count, counts.Sum(), counts);
    }

    public static int CountCiphertextsInBatch(byte[] batchBytes)
    {
        foreach (var envelope in ParseJsonObjects(batchBytes))
        {
            if (TryGetDataArray(envelope, out var data))
            {
                return data.GetArrayLength();
            }
        }

        return 0;
    }

    public static IReadOnlyList<string> ExtractCiphertexts(byte[] datasetBytes)
    {
        var ciphertexts = new List<string>();
        foreach (var envelope in ParseJsonObjects(datasetBytes))
        {
            if (!TryGetDataArray(envelope, out var data))
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

        return ciphertexts;
    }

    public static bool LooksLikeBatchEnvelopes(byte[] datasetBytes)
    {
        var text = Encoding.UTF8.GetString(datasetBytes).TrimStart();
        return text.StartsWith('{') && text.Contains("\"data\"", StringComparison.Ordinal);
    }

    public static IReadOnlyList<JsonElement> ParseJsonObjects(byte[] utf8Bytes)
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

    private static bool TryGetDataArray(JsonElement envelope, out JsonElement data)
    {
        if (envelope.ValueKind == JsonValueKind.Object &&
            envelope.TryGetProperty("data", out data) &&
            data.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        data = default;
        return false;
    }
}
