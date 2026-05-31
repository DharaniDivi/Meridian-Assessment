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



public sealed class Layer1Service

{

    private const string MetaFileName = "layer1-meta.json";



    private readonly AssessmentHttpClient _client;

    private readonly AssessmentOptions _options;

    private readonly ILogger<Layer1Service> _logger;



    public Layer1Service(

        AssessmentHttpClient client,

        IOptions<AssessmentOptions> options,

        ILogger<Layer1Service> logger)

    {

        _client = client;

        _options = options.Value;

        _logger = logger;

    }



    public async Task<Layer1Result> FetchAndHashAsync(CancellationToken cancellationToken = default)

    {

        try

        {

            Directory.CreateDirectory(_options.DataDirectory);

            var datasetPath = Path.Combine(_options.DataDirectory, "dataset.bin");



            var (indexResponse, indexStream) = await _client.GetDatasetStreamAsync(cancellationToken);

            await _client.SaveDatasetHeadersAsync(indexResponse, _options.DataDirectory, cancellationToken);



            byte[]? indexBytes = null;

            await using (indexStream)

            {

                using var memory = new MemoryStream();

                await indexStream.CopyToAsync(memory, cancellationToken);

                indexBytes = memory.ToArray();

                if (indexBytes.Length > 0)

                {

                    await File.WriteAllBytesAsync(

                        Path.Combine(_options.DataDirectory, "dataset-index.bin"),

                        indexBytes,

                        cancellationToken);

                }

            }



            var linkHeader = indexResponse.Headers.TryGetValues("Link", out var links)

                ? string.Join(", ", links)

                : null;



            var batchPath = LinkHeaderParser.GetBatchPath(linkHeader);

            if (batchPath is null)

            {

                return await FinalizeSingleDownloadAsync(datasetPath, indexBytes ?? Array.Empty<byte>(), indexResponse.Headers.ETag?.Tag, cancellationToken);

            }



            return await FetchBatchedAndHashAsync(datasetPath, batchPath, indexBytes, cancellationToken);

        }

        catch (AssessmentApiException ex)

        {

            _logger.LogError(ex, "Layer 1 API error {StatusCode}", ex.StatusCode);

            var hint = ex.StatusCode is 503 or 429

                ? " Wait a minute and try Run Layer 1 again."

                : string.Empty;

            return new Layer1Result(false, null, 0, ex.Message + hint);

        }

        catch (Exception ex)

        {

            _logger.LogError(ex, "Layer 1 failed");

            return new Layer1Result(false, null, 0, ex.Message);

        }

    }



    private async Task<Layer1Result> FetchBatchedAndHashAsync(

        string datasetPath,

        string firstBatchPath,

        byte[]? indexBytes,

        CancellationToken cancellationToken)

    {

        var stats = await _client.GetDatasetStatsAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(stats?.RawJson))

        {

            await File.WriteAllTextAsync(

                Path.Combine(_options.DataDirectory, "stats.json"),

                stats.RawJson,

                cancellationToken);

        }



        var range = LinkHeaderParser.ParseRangeFromPath(firstBatchPath);

        var batchSize = range.HasValue ? range.Value.End - range.Value.Start + 1 : stats?.BatchSize ?? 100;

        var currentStart = range?.Start ?? 0;

        var totalRecords = stats?.TotalRecords;



        _logger.LogInformation(

            "Layer 1: batched download starting at range {Start}, batchSize={BatchSize}, totalRecords={Total}",

            currentStart, batchSize, totalRecords);



        var batches = new List<byte[]>();
        var batchEtags = new List<string>();
        var batchCount = 0;



        while (true)

        {

            if (totalRecords.HasValue && currentStart >= totalRecords.Value)

            {

                break;

            }



            var currentEnd = totalRecords.HasValue

                ? Math.Min(currentStart + batchSize - 1, totalRecords.Value - 1)

                : currentStart + batchSize - 1;



            var path = LinkHeaderParser.BuildBatchPath(currentStart, currentEnd);



            try

            {

                var (batchResponse, batchStream) = await _client.GetPathStreamAsync(path, cancellationToken);

                await using (batchStream)

                {

                    using var memory = new MemoryStream();

                    await batchStream.CopyToAsync(memory, cancellationToken);

                    var batchBytes = memory.ToArray();

                    if (batchBytes.Length == 0)

                    {

                        break;

                    }



                    batches.Add(batchBytes);
                    batchCount++;

                    var batchEtag = EtagParser.ExtractSha256Hex(batchResponse.Headers.ETag?.Tag);
                    if (!string.IsNullOrWhiteSpace(batchEtag))
                    {
                        batchEtags.Add(batchEtag);
                    }

                    _logger.LogInformation(

                        "Layer 1: batch {Batch} range {Start}-{End} downloaded {Bytes} bytes, ETag={ETag}",

                        batchCount,

                        currentStart,

                        currentEnd,

                        batchBytes.Length,

                        batchResponse.Headers.ETag?.Tag);



                    var nextLink = batchResponse.Headers.TryGetValues("Link", out var batchLinks)

                        ? string.Join(", ", batchLinks)

                        : null;

                    var nextBatch = LinkHeaderParser.GetBatchPath(nextLink);

                    var nextRange = nextBatch is not null ? LinkHeaderParser.ParseRangeFromPath(nextBatch) : null;

                    if (nextRange.HasValue && nextRange.Value.Start > currentStart)

                    {

                        currentStart = nextRange.Value.Start;

                        batchSize = nextRange.Value.End - nextRange.Value.Start + 1;

                        continue;

                    }

                }

            }

            catch (AssessmentApiException ex) when (ex.StatusCode == 404 && batchCount > 0)

            {

                break;

            }



            if (_options.BatchDelayMs > 0 && batchCount > 0)

            {

                await Task.Delay(_options.BatchDelayMs, cancellationToken);

            }



            currentStart += batchSize;

            if (totalRecords.HasValue && currentStart >= totalRecords.Value)

            {

                break;

            }

        }



        if (batches.Count == 0)

        {

            return new Layer1Result(false, null, 0, "No dataset batches were downloaded.");

        }



        byte[]? fullRangeBytes = null;
        string? fullRangeEtag = null;
        if (totalRecords is > 0)
        {
            var fullPath = LinkHeaderParser.BuildBatchPath(0, totalRecords.Value - 1);
            try
            {
                var (fullResponse, fullStream) = await _client.GetPathStreamAsync(fullPath, cancellationToken);
                await using (fullStream)
                {
                    using var memory = new MemoryStream();
                    await fullStream.CopyToAsync(memory, cancellationToken);
                    fullRangeBytes = memory.ToArray();
                    fullRangeEtag = EtagParser.ExtractSha256Hex(fullResponse.Headers.ETag?.Tag);
                    _logger.LogInformation(
                        "Layer 1: full-range probe range 0-{End} downloaded {Bytes} bytes, ETag={ETag}, bodySha256={Hash}",
                        totalRecords.Value - 1,
                        fullRangeBytes.Length,
                        fullResponse.Headers.ETag?.Tag,
                        ContentHasher.Sha256Hex(fullRangeBytes));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Layer 1: full-range probe failed for {Path}", fullPath);
            }
        }



        var integrity = DatasetIntegrityHasher.Compute(batches, indexBytes);

        var canonicalBytes = DatasetIntegrityHasher.BuildCanonicalDatasetBytes(batches);
        integrity = AppendExtraHashes(integrity, canonicalBytes, batchEtags, fullRangeBytes, fullRangeEtag);

        var batchesRawPath = Path.Combine(_options.DataDirectory, "dataset-batches.raw");
        await File.WriteAllBytesAsync(batchesRawPath, DatasetBatchMerger.ConcatenateRaw(batches), cancellationToken);

        if (batchEtags.Count > 0)
        {
            var etagMeta = new Dictionary<string, object?>
            {
                ["batchEtags"] = batchEtags,
                ["batchEtagConcat"] = ContentHasher.Sha256Hex(Encoding.UTF8.GetBytes(string.Join(string.Empty, batchEtags)))
            };
            await File.WriteAllTextAsync(
                Path.Combine(_options.DataDirectory, "dataset-batch-etags.json"),
                JsonSerializer.Serialize(etagMeta, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
        }

        await File.WriteAllBytesAsync(datasetPath, canonicalBytes, cancellationToken);

        await SaveLayer1MetaAsync(batchCount, integrity, totalRecords, cancellationToken);



        _logger.LogInformation(

            "Layer 1: complete. {Batches} batches, {Records} ciphertexts, {Bytes} bytes, primary={Format}, SHA-256={Hash}",

            batchCount,

            integrity.CiphertextCount,

            canonicalBytes.Length,

            integrity.PrimaryFormat,

            integrity.PrimaryHash);



        string? message = null;

        if (totalRecords.HasValue && integrity.CiphertextCount != totalRecords.Value)

        {

            message = $"Downloaded {integrity.CiphertextCount} ciphertexts but stats reports {totalRecords.Value}.";

        }



        return new Layer1Result(true, integrity.PrimaryHash, canonicalBytes.Length, message, integrity.All);

    }



    private async Task<Layer1Result> FinalizeSingleDownloadAsync(

        string datasetPath,

        byte[] bytes,

        string? etag,

        CancellationToken cancellationToken)

    {

        var hashHex = ContentHasher.Sha256Hex(bytes);

        await File.WriteAllBytesAsync(datasetPath, bytes, cancellationToken);

        await SaveLayer1MetaAsync(

            1,

            new DatasetIntegrityHashes(hashHex, "singleDownload", DatasetFormatParser.CountCiphertextsInBatch(bytes), new Dictionary<string, string>

            {

                ["singleDownload"] = hashHex

            }),

            null,

            cancellationToken);



        _logger.LogInformation(

            "Layer 1: single download {Bytes} bytes, SHA-256={Hash}, ETag={ETag}",

            bytes.Length,

            hashHex,

            etag);



        return new Layer1Result(true, hashHex, bytes.Length, null, new Dictionary<string, string> { ["singleDownload"] = hashHex });

    }



    private async Task SaveLayer1MetaAsync(

        int batchCount,

        DatasetIntegrityHashes integrity,

        int? expectedRecords,

        CancellationToken cancellationToken)

    {

        var meta = new Dictionary<string, object?>

        {

            ["batchCount"] = batchCount,

            ["recordCount"] = integrity.CiphertextCount,

            ["expectedRecords"] = expectedRecords,

            ["primaryFormat"] = integrity.PrimaryFormat,

            ["hashPrimary"] = integrity.PrimaryHash,

            ["hashes"] = integrity.All

        };



        var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(Path.Combine(_options.DataDirectory, MetaFileName), json, cancellationToken);

    }



    public async Task<Layer1Result> RehashFromCacheAsync(CancellationToken cancellationToken = default)
    {
        var datasetPath = Path.Combine(_options.DataDirectory, "dataset.bin");
        if (!File.Exists(datasetPath))
        {
            return new Layer1Result(false, null, 0, "Dataset cache not found. Run Layer 1 first.");
        }

        var batchesRawPath = Path.Combine(_options.DataDirectory, "dataset-batches.raw");
        if (!File.Exists(batchesRawPath))
        {
            var cachedDataset = await File.ReadAllBytesAsync(datasetPath, cancellationToken);
            if (ContainsJsonEscapedPlus(cachedDataset))
            {
                return new Layer1Result(
                    false,
                    null,
                    0,
                    "Cached dataset.bin contains JSON-escaped plus signs (\\u002B). Rehash cannot fix this — run Layer 1 again (POST /api/layers/1/run) to re-download raw batch responses.");
            }
        }

        var batchSourcePath = File.Exists(batchesRawPath) ? batchesRawPath : datasetPath;
        var batchBytes = await File.ReadAllBytesAsync(batchSourcePath, cancellationToken);
        var batches = SplitCachedBatches(batchBytes);
        if (batches.Count == 0)
        {
            batches.Add(batchBytes);
        }

        byte[]? indexBytes = null;
        var indexPath = Path.Combine(_options.DataDirectory, "dataset-index.bin");
        if (File.Exists(indexPath))
        {
            indexBytes = await File.ReadAllBytesAsync(indexPath, cancellationToken);
        }

        var integrity = DatasetIntegrityHasher.Compute(batches, indexBytes);
        var canonicalBytes = DatasetIntegrityHasher.BuildCanonicalDatasetBytes(batches);
        integrity = AppendExtraHashes(integrity, canonicalBytes, ReadSavedBatchEtags(), null, null);
        int? expectedRecords = null;
        var statsPath = Path.Combine(_options.DataDirectory, "stats.json");
        if (File.Exists(statsPath))
        {
            var stats = AssessmentHttpClient.ParseDatasetStats(await File.ReadAllTextAsync(statsPath, cancellationToken));
            expectedRecords = stats?.TotalRecords;
        }

        await File.WriteAllBytesAsync(datasetPath, canonicalBytes, cancellationToken);
        await SaveLayer1MetaAsync(batches.Count, integrity, expectedRecords, cancellationToken);

        return new Layer1Result(
            true,
            integrity.PrimaryHash,
            canonicalBytes.Length,
            $"Rehashed locally using {integrity.PrimaryFormat}.",
            integrity.All);
    }

    private static List<byte[]> SplitCachedBatches(byte[] datasetBytes)
    {
        var batches = new List<byte[]>();
        var start = 0;
        var depth = 0;
        for (var i = 0; i < datasetBytes.Length; i++)
        {
            if (datasetBytes[i] == (byte)'{')
            {
                if (depth == 0)
                {
                    start = i;
                }

                depth++;
            }
            else if (datasetBytes[i] == (byte)'}')
            {
                depth--;
                if (depth == 0)
                {
                    batches.Add(datasetBytes[start..(i + 1)]);
                }
            }
        }

        return batches;
    }

    private static bool ContainsJsonEscapedPlus(byte[] bytes)
        => bytes.AsSpan().IndexOf("\\u002B"u8) >= 0;

    public async Task<string?> GetSubmitHashAsync(CancellationToken cancellationToken = default)

    {

        var meta = await ReadMetaAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(meta?.PrimaryHash))

        {

            return meta.PrimaryHash;

        }



        return await ComputeHashFromCacheAsync(cancellationToken);

    }



    public async Task<Layer1Meta?> ReadMetaAsync(CancellationToken cancellationToken = default)

    {

        var path = Path.Combine(_options.DataDirectory, MetaFileName);

        if (!File.Exists(path))

        {

            return null;

        }



        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));

        var root = doc.RootElement;



        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("hashes", out var hashElement) && hashElement.ValueKind == JsonValueKind.Object)

        {

            foreach (var property in hashElement.EnumerateObject())

            {

                if (property.Value.ValueKind == JsonValueKind.String)

                {

                    var value = property.Value.GetString();

                    if (!string.IsNullOrWhiteSpace(value))

                    {

                        hashes[property.Name] = value;

                    }

                }

            }

        }



        foreach (var legacy in new[] { "hashPrimary", "hashRawConcat", "hashMergedEnvelope", "hashCiphertextArray" })

        {

            if (root.TryGetProperty(legacy, out var legacyValue) && legacyValue.ValueKind == JsonValueKind.String)

            {

                var hash = legacyValue.GetString();

                if (!string.IsNullOrWhiteSpace(hash))

                {

                    hashes[legacy] = hash;

                }

            }

        }



        var primaryFormat = root.TryGetProperty("primaryFormat", out var formatProp)

            ? formatProp.GetString() ?? "rawConcat"

            : "rawConcat";



        var primaryHash = root.TryGetProperty("hashPrimary", out var primaryProp)

            ? primaryProp.GetString()

            : hashes.GetValueOrDefault(primaryFormat) ?? hashes.Values.FirstOrDefault();



        int? recordCount = root.TryGetProperty("recordCount", out var countProp) && countProp.TryGetInt32(out var count)

            ? count

            : null;



        int? batchCount = root.TryGetProperty("batchCount", out var batchProp) && batchProp.TryGetInt32(out var batches)

            ? batches

            : null;



        return new Layer1Meta(primaryHash, primaryFormat, recordCount, batchCount, hashes);

    }



    public async Task<string?> ComputeHashFromCacheAsync(CancellationToken cancellationToken = default)

    {

        var submitHash = await GetSubmitHashAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(submitHash))

        {

            return submitHash;

        }



        var datasetPath = Path.Combine(_options.DataDirectory, "dataset.bin");

        if (!File.Exists(datasetPath))

        {

            return null;

        }



        await using var stream = File.OpenRead(datasetPath);

        return await ContentHasher.Sha256HexAsync(stream, cancellationToken);

    }



    public async Task<IReadOnlyList<string>> GetHashCandidatesAsync(CancellationToken cancellationToken = default)

    {

        var candidates = new List<string>();

        var meta = await ReadMetaAsync(cancellationToken);

        if (meta is not null)

        {

            if (!string.IsNullOrWhiteSpace(meta.PrimaryHash))

            {

                candidates.Add(meta.PrimaryHash);

            }



            foreach (var hash in meta.Hashes.Values)

            {

                candidates.Add(hash);

            }

        }



        var etag = ReadSavedEtag();

        if (!string.IsNullOrWhiteSpace(etag))

        {

            candidates.Add(etag);

        }



        return candidates
            .Where(static c => c.Length == 64 && c.All(static ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    }



    public async Task<IReadOnlyDictionary<string, string>> GetNamedHashCandidatesAsync(

        CancellationToken cancellationToken = default)

    {

        var meta = await ReadMetaAsync(cancellationToken);

        if (meta is null)

        {

            return new Dictionary<string, string>();

        }



        return meta.Hashes

            .Where(static kv => IsSha256Hex(kv.Value) && !IsMetadataKey(kv.Key))

            .ToDictionary(static kv => kv.Key, static kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    }



    public async Task<(bool Success, string? WinningHash, string? WinningFormat, IReadOnlyList<string> Attempts)>

        TrySubmitAllCandidatesAsync(

            SubmissionService submission,

            CancellationToken cancellationToken = default)

    {

        var named = await GetNamedHashCandidatesAsync(cancellationToken);

        var attempts = new List<string>();

        foreach (var (format, hash) in named)

        {

            var result = await submission.SubmitLayer1Async(hash, notes: $"auto-try:{format}", cancellationToken);

            attempts.Add($"{format}={hash} -> {(result.Success ? "accepted" : "rejected")}");

            if (result.Success)

            {

                return (true, hash, format, attempts);

            }

        }



        return (false, null, null, attempts);

    }



    private static bool IsSha256Hex(string value)

        => value.Length == 64 && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');



    private static bool IsMetadataKey(string key)

        => key.Equals("indexCiphertextCount", StringComparison.OrdinalIgnoreCase)

           || key.Equals("indexMatchesFirstBatch", StringComparison.OrdinalIgnoreCase);



    private static DatasetIntegrityHashes AppendExtraHashes(

        DatasetIntegrityHashes integrity,

        byte[] canonicalBytes,

        IReadOnlyList<string> batchEtags,

        byte[]? fullRangeBytes,

        string? fullRangeEtag)

    {

        var all = new Dictionary<string, string>(integrity.All, StringComparer.OrdinalIgnoreCase)

        {

            ["canonicalFileBytes"] = ContentHasher.Sha256Hex(canonicalBytes)

        };



        if (batchEtags.Count > 0)

        {

            all["batchDigestBinaryConcat"] = ContentHasher.Sha256Hex(DatasetIntegrityHasher.BuildBatchDigestBinaryConcat(batchEtags));

            all["batchEtagConcat"] = ContentHasher.Sha256Hex(Encoding.UTF8.GetBytes(string.Join(string.Empty, batchEtags)));

            all["batchEtagNewlineJoin"] = ContentHasher.Sha256Hex(Encoding.UTF8.GetBytes(string.Join('\n', batchEtags)));

        }



        if (fullRangeBytes is { Length: > 0 })

        {

            all["fullRangeBody"] = ContentHasher.Sha256Hex(fullRangeBytes);

            if (!string.IsNullOrWhiteSpace(fullRangeEtag))

            {

                all["fullRangeEtag"] = fullRangeEtag;

            }

        }



        var primaryFormat = all.ContainsKey("fullRangeBody") ? "fullRangeBody"
            : all.ContainsKey("decodedCipherConcat") ? "decodedCipherConcat"
            : "rawConcat";

        var primaryHash = all[primaryFormat];

        return new DatasetIntegrityHashes(primaryHash, primaryFormat, integrity.CiphertextCount, all);

    }



    private IReadOnlyList<string> ReadSavedBatchEtags()

    {

        var path = Path.Combine(_options.DataDirectory, "dataset-batch-etags.json");

        if (!File.Exists(path))

        {

            return Array.Empty<string>();

        }



        try

        {

            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            if (doc.RootElement.TryGetProperty("batchEtags", out var etags) &&

                etags.ValueKind == JsonValueKind.Array)

            {

                return etags.EnumerateArray()

                    .Select(e => e.GetString())

                    .Where(e => !string.IsNullOrWhiteSpace(e))

                    .Select(e => e!)

                    .ToList();

            }

        }

        catch

        {

            // ignore

        }



        return Array.Empty<string>();

    }



    private string? ReadSavedEtag()

    {

        var path = Path.Combine(_options.DataDirectory, "dataset-headers.json");

        if (!File.Exists(path))

        {

            return null;

        }



        try

        {

            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            if (doc.RootElement.TryGetProperty("ETag", out var etag))

            {

                return EtagParser.ExtractSha256Hex(etag.GetString());

            }

        }

        catch

        {

            // ignore

        }



        return null;

    }

}



public sealed record Layer1Meta(

    string? PrimaryHash,

    string PrimaryFormat,

    int? RecordCount,

    int? BatchCount,

    IReadOnlyDictionary<string, string> Hashes);


