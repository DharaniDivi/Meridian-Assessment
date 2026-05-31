using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var dataPath = args.Length > 0 ? args[0] : @"C:\AA-PROJECT\src\Assessment.Api\data\dataset.bin";
if (!File.Exists(dataPath))
{
    Console.WriteLine($"Missing: {dataPath}");
    return 1;
}

var bytes = await File.ReadAllBytesAsync(dataPath);
Console.WriteLine($"Size: {bytes.Length}");
Console.WriteLine($"SHA256 raw: {Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}");

var text = Encoding.UTF8.GetString(bytes);
Console.WriteLine($"Starts: {text[..Math.Min(120, text.Length)].Replace('\n', ' ')}");
Console.WriteLine($"Ends: {text[Math.Max(0, text.Length - 120)..].Replace('\n', ' ')}");

var arrayCount = text.Split("][", StringSplitOptions.None).Length;
Console.WriteLine($"'][[' splits: {arrayCount - 1} (arrays concatenated: ~{arrayCount})");

// Try merge arrays
var merged = MergeJsonArrays(text);
if (merged is not null)
{
    var mergedBytes = Encoding.UTF8.GetBytes(merged);
    Console.WriteLine($"Merged array size: {mergedBytes.Length}");
    Console.WriteLine($"SHA256 merged: {Convert.ToHexString(SHA256.HashData(mergedBytes)).ToLowerInvariant()}");

    using var doc = JsonDocument.Parse(merged);
    Console.WriteLine($"Merged record count: {doc.RootElement.GetArrayLength()}");
}

// Try NDJSON normalize
var ndjson = ToNdjson(text);
if (ndjson is not null)
{
    var ndBytes = Encoding.UTF8.GetBytes(ndjson);
    Console.WriteLine($"NDJSON size: {ndBytes.Length}");
    Console.WriteLine($"SHA256 ndjson: {Convert.ToHexString(SHA256.HashData(ndBytes)).ToLowerInvariant()}");
    Console.WriteLine($"NDJSON lines: {ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length}");
}

return 0;

static string? MergeJsonArrays(string text)
{
    var trimmed = text.Trim();
    if (!trimmed.Contains("]["))
    {
        return trimmed.StartsWith('[') ? trimmed : null;
    }

    var parts = trimmed.Split("][", StringSplitOptions.None);
    var records = new List<JsonElement>();
    for (var i = 0; i < parts.Length; i++)
    {
        var chunk = parts[i];
        if (i == 0) chunk += "]";
        else if (i == parts.Length - 1) chunk = "[" + chunk;
        else chunk = "[" + chunk + "]";

        using var doc = JsonDocument.Parse(chunk);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            records.Add(el.Clone());
        }
    }

    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
    {
        writer.WriteStartArray();
        foreach (var el in records)
        {
            el.WriteTo(writer);
        }
        writer.WriteEndArray();
    }

    return Encoding.UTF8.GetString(stream.ToArray());
}

static string? ToNdjson(string text)
{
    var merged = MergeJsonArrays(text);
    if (merged is null)
    {
        return null;
    }

    using var doc = JsonDocument.Parse(merged);
    var lines = doc.RootElement.EnumerateArray().Select(e => e.GetRawText());
    return string.Join('\n', lines);
}
