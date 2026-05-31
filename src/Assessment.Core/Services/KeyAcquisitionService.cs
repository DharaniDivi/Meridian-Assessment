using System.Text.Json;
using Assessment.Core.Configuration;
using Assessment.Core.Utilities;
using Assessment.Core.Http;
using Assessment.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Assessment.Core.Services;

public sealed class KeyAcquisitionService
{
    private static readonly string[] GetKeyPaths =
    [
        "api/v1/decryption-key",
        "api/v1/decryption_key",
        "api/v1/layer2/key",
        "api/v1/layer/2/key",
        "api/v1/layers/2/key",
        "api/v1/layer1/key",
        "api/v1/layers/1/key",
        "api/v1/credential",
        "api/v1/credentials",
        "api/v1/unlock",
        "api/v1/keys",
        "api/v1/key",
        "api/v1/transcript",
        "api/v1/layer/2",
        "api/v1/layers/2",
        "api/v1/progress",
        "api/v1/status"
    ];

    private readonly AssessmentHttpClient _client;
    private readonly AssessmentOptions _options;
    private readonly ILogger<KeyAcquisitionService> _logger;

    public KeyAcquisitionService(
        AssessmentHttpClient client,
        IOptions<AssessmentOptions> options,
        ILogger<KeyAcquisitionService> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(string? Key, string Source, string? Error)> AcquireKeyAsync(
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.DataDirectory);

        var cached = ReadCachedKey();
        if (cached is not null)
        {
            return (cached, "cache:decryption-key.txt", null);
        }

        var fromSubmission = ReadKeyFromSubmission();
        if (fromSubmission is not null)
        {
            await SaveKeyAsync(fromSubmission, cancellationToken);
            return (fromSubmission, "content_hash-submission-response", null);
        }

        var fromSubmissionHeaders = ReadKeyFromSubmissionHeaders();
        if (fromSubmissionHeaders is not null)
        {
            await SaveKeyAsync(fromSubmissionHeaders, cancellationToken);
            return (fromSubmissionHeaders, "content_hash-submission-headers", null);
        }

        var fromHeaders = ReadKeyFromDatasetHeaders();
        if (fromHeaders is not null)
        {
            await SaveKeyAsync(fromHeaders, cancellationToken);
            return (fromHeaders, "dataset-response-headers", null);
        }

        if (HasLayer1Success())
        {
            var (liveKey, liveSource) = await RefreshLiveKeySourcesAsync(cancellationToken);
            if (liveKey is not null)
            {
                await SaveKeyAsync(liveKey, cancellationToken);
                return (liveKey, liveSource, null);
            }
        }

        foreach (var path in GetKeyPaths)
        {
            var (key, error, statusCode) = await _client.TryGetKeyFromPathAsync(path, cancellationToken);
            if (key is not null)
            {
                await SaveKeyAsync(key, cancellationToken);
                return (key, $"GET {path}", null);
            }

            if (statusCode is not 404 and not null)
            {
                _logger.LogWarning("Key probe {Path} returned {Status}: {Error}", path, statusCode, error);
            }
        }

        return (null, "none", "No decryption key found. Layer 1 passed — try POST /api/layers/2/acquire-key to probe live endpoints.");
    }

    public async Task<IReadOnlyList<(string Key, string Source)>> GetDerivedKeyCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<(string Key, string Source)>();

        void Add(string? key, string source)
        {
            if (LooksLikeKey(key) && !candidates.Any(c => c.Key == key))
            {
                candidates.Add((key!, source));
            }
        }

        Add(ReadCachedKey(), "cache:decryption-key.txt");
        Add(ReadKeyFromSubmission(), "submission-content_hash.json");
        Add(ReadKeyFromSubmissionHeaders(), "submission-content_hash-headers.json");
        Add(ReadKeyFromDatasetHeaders(), "dataset-headers.json");

        var metaPath = Path.Combine(_options.DataDirectory, "layer1-meta.json");
        if (File.Exists(metaPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(metaPath, cancellationToken));
                var root = doc.RootElement;
                if (root.TryGetProperty("hashPrimary", out var primary) &&
                    primary.ValueKind == JsonValueKind.String)
                {
                    Add(primary.GetString(), "layer1-meta.hashPrimary");
                }

                if (root.TryGetProperty("hashes", out var hashes) &&
                    hashes.ValueKind == JsonValueKind.Object)
                {
                    foreach (var name in new[] { "decodedCipherConcat", "content_hash", "hashPrimary" })
                    {
                        if (hashes.TryGetProperty(name, out var hash) &&
                            hash.ValueKind == JsonValueKind.String)
                        {
                            Add(hash.GetString(), $"layer1-meta.{name}");
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        Add(ReadSubmissionId(), "submission_id");

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            Add(_options.ApiKey, "Assessment:ApiKey");
            if (_options.ApiKey.StartsWith("sa_", StringComparison.OrdinalIgnoreCase))
            {
                Add(_options.ApiKey["sa_".Length..], "Assessment:ApiKey (without sa_ prefix)");
            }
        }

        var contentHash = ReadContentHash();
        var submissionId = ReadSubmissionId();
        if (!string.IsNullOrWhiteSpace(contentHash) && !string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            foreach (var (key, source) in Layer2DecryptionEngine.DeriveHmacKeyCandidates(
                         _options.ApiKey,
                         contentHash,
                         submissionId))
            {
                Add(key, source);
            }
        }

        if (HasLayer1Success())
        {
            var (liveKey, liveSource) = await RefreshLiveKeySourcesAsync(cancellationToken);
            Add(liveKey, liveSource);
        }

        foreach (var path in GetKeyPaths)
        {
            var (key, _, _) = await _client.TryGetKeyFromPathAsync(path, cancellationToken);
            Add(key, $"GET {path}");
        }

        return candidates;
    }

    public async Task<IReadOnlyList<string>> ProbeKeySourcesAsync(CancellationToken cancellationToken = default)
    {
        var probes = new List<string>();
        foreach (var path in GetKeyPaths)
        {
            var (key, error, statusCode) = await _client.TryGetKeyFromPathAsync(path, cancellationToken);
            probes.Add($"{path}: {(statusCode?.ToString() ?? "?")} key={(key is not null)} {error}");
        }

        if (HasLayer1Success())
        {
            var (liveKey, liveSource) = await RefreshLiveKeySourcesAsync(cancellationToken);
            probes.Add($"liveRefresh: {(liveKey is not null ? $"found via {liveSource}" : "no key")}");
        }

        return probes;
    }

    private async Task<(string? Key, string Source)> RefreshLiveKeySourcesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var (transcriptResponse, transcriptBody) = await _client.TryGetBodyFromPathAsync(
                "api/v1/transcript",
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(transcriptBody))
            {
                await File.WriteAllTextAsync(
                    Path.Combine(_options.DataDirectory, "transcript.json"),
                    transcriptBody,
                    cancellationToken);
                var transcriptKey = KeyResponseParser.ExtractKeyFromJson(transcriptBody);
                if (LooksLikeKey(transcriptKey))
                {
                    return (transcriptKey!, "GET api/v1/transcript body");
                }

                var transcriptHeaderKey = KeyResponseParser.FindKeyInHeaders(
                    transcriptResponse.Headers.Concat(transcriptResponse.Content.Headers)
                        .Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value)));
                if (LooksLikeKey(transcriptHeaderKey))
                {
                    return (transcriptHeaderKey!, "GET api/v1/transcript response headers");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transcript key probe failed");
        }

        try
        {
            var (batchResponse, batchStream) = await _client.GetPathStreamAsync(
                LinkHeaderParser.BuildBatchPath(0, 99),
                cancellationToken);
            await using (batchStream)
            {
                await batchStream.CopyToAsync(Stream.Null, cancellationToken);
            }

            var batchHeaderKey = KeyResponseParser.FindKeyInHeaders(
                batchResponse.Headers.Concat(batchResponse.Content.Headers)
                    .Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value)));
            if (LooksLikeKey(batchHeaderKey))
            {
                return (batchHeaderKey!, "GET api/v1/dataset batch 0-99 response headers");
            }

            var batchLink = batchResponse.Headers.TryGetValues("Link", out var batchLinks)
                ? string.Join(", ", batchLinks)
                : null;
            foreach (var path in LinkHeaderParser.GetAllPaths(batchLink))
            {
                if (path.Contains("batch", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var (key, _, statusCode) = await _client.TryGetKeyFromPathAsync(path, cancellationToken);
                if (key is not null)
                {
                    return (key, $"GET {path} (from batch Link header)");
                }

                _logger.LogInformation("Batch link probe {Path} -> {Status}", path, statusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch header refresh for key failed");
        }

        try
        {
            var (indexResponse, indexStream) = await _client.GetDatasetStreamAsync(cancellationToken);
            await _client.SaveDatasetHeadersAsync(indexResponse, _options.DataDirectory, cancellationToken);
            await using (indexStream)
            {
                await indexStream.CopyToAsync(Stream.Null, cancellationToken);
            }

            var headerKey = KeyResponseParser.FindKeyInHeaders(
                indexResponse.Headers.Concat(indexResponse.Content.Headers)
                    .Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value)));
            if (LooksLikeKey(headerKey))
            {
                return (headerKey!, "GET api/v1/dataset response headers");
            }

            var linkHeader = indexResponse.Headers.TryGetValues("Link", out var links)
                ? string.Join(", ", links)
                : null;
            foreach (var path in LinkHeaderParser.GetAllPaths(linkHeader))
            {
                if (path.Contains("batch", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("stats", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var (key, _, statusCode) = await _client.TryGetKeyFromPathAsync(path, cancellationToken);
                if (key is not null)
                {
                    return (key, $"GET {path} (from dataset Link header)");
                }

                _logger.LogInformation("Link probe {Path} -> {Status}", path, statusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live dataset refresh for key failed");
        }

        try
        {
            var stats = await _client.GetDatasetStatsAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(stats?.RawJson))
            {
                await File.WriteAllTextAsync(
                    Path.Combine(_options.DataDirectory, "stats.json"),
                    stats.RawJson,
                    cancellationToken);
                var statsKey = KeyResponseParser.ExtractKeyFromJson(stats.RawJson);
                if (LooksLikeKey(statsKey))
                {
                    return (statsKey!, "GET api/v1/stats body");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stats key probe failed");
        }

        var submissionId = ReadSubmissionId();
        if (!string.IsNullOrWhiteSpace(submissionId))
        {
            foreach (var path in new[]
                     {
                         $"api/v1/submissions/{submissionId}",
                         $"api/v1/submission/{submissionId}",
                         $"api/v1/submit/{submissionId}"
                     })
            {
                var (key, _, statusCode) = await _client.TryGetKeyFromPathAsync(path, cancellationToken);
                if (key is not null)
                {
                    return (key, $"GET {path}");
                }

                _logger.LogInformation("Submission probe {Path} -> {Status}", path, statusCode);
            }
        }

        return (null, "none");
    }

    public async Task SaveKeyFromSubmissionResponseAsync(
        string responseBody,
        bool submissionSucceeded,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.DataDirectory);
        var responsePath = Path.Combine(_options.DataDirectory, "submission-content_hash.json");
        await File.WriteAllTextAsync(responsePath, responseBody, cancellationToken);

        if (!submissionSucceeded)
        {
            var invalidKeyPath = Path.Combine(_options.DataDirectory, "decryption-key.txt");
            if (File.Exists(invalidKeyPath))
            {
                var cached = File.ReadAllText(invalidKeyPath).Trim();
                if (!KeyResponseParser.LooksLikeKeyMaterial(cached))
                {
                    File.Delete(invalidKeyPath);
                }
            }

            return;
        }

        var parsed = SubmissionResponseParser.Parse(responseBody);
        if (parsed?.Correct == true && !string.IsNullOrWhiteSpace(parsed.Key))
        {
            await SaveKeyAsync(parsed.Key, cancellationToken);
            return;
        }

        var fallbackKey = KeyResponseParser.ExtractKeyFromJson(responseBody);
        if (LooksLikeKey(fallbackKey))
        {
            await SaveKeyAsync(fallbackKey!, cancellationToken);
        }
    }

    public async Task SaveKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.DataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(_options.DataDirectory, "decryption-key.txt"),
            key.Trim(),
            cancellationToken);
    }

    private string? ReadCachedKey()
    {
        var path = Path.Combine(_options.DataDirectory, "decryption-key.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private string? ReadKeyFromSubmission()
    {
        foreach (var file in new[] { "submission-content_hash.json", "submission-layer1.json" })
        {
            var path = Path.Combine(_options.DataDirectory, file);
            if (!File.Exists(path))
            {
                continue;
            }

            var key = KeyResponseParser.ExtractKeyFromJson(File.ReadAllText(path));
            if (LooksLikeKey(key))
            {
                return key;
            }
        }

        return null;
    }

    private string? ReadKeyFromSubmissionHeaders()
    {
        var path = Path.Combine(_options.DataDirectory, "submission-content_hash-headers.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var value = property.Value.GetString();
                if (LooksLikeKey(value))
                {
                    return value;
                }

                var extracted = KeyResponseParser.ExtractKeyFromJson(value ?? string.Empty);
                if (LooksLikeKey(extracted))
                {
                    return extracted;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private string? ReadKeyFromDatasetHeaders()
    {
        var path = Path.Combine(_options.DataDirectory, "dataset-headers.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var value = property.Value.GetString();
                if (LooksLikeKey(value))
                {
                    return value;
                }

                var extracted = KeyResponseParser.ExtractKeyFromJson(value ?? string.Empty);
                if (LooksLikeKey(extracted))
                {
                    return extracted;
                }
            }
        }
        catch
        {
            // ignore malformed header cache
        }

        return null;
    }

    private bool HasLayer1Success()
    {
        var path = Path.Combine(_options.DataDirectory, "submission-content_hash.json");
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("correct", out var correct) &&
                   correct.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private string? ReadSubmissionId()
    {
        var path = Path.Combine(_options.DataDirectory, "submission-content_hash.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("submission_id", out var id))
            {
                return id.GetString();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private string? ReadContentHash()
    {
        var metaPath = Path.Combine(_options.DataDirectory, "layer1-meta.json");
        if (File.Exists(metaPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (doc.RootElement.TryGetProperty("hashPrimary", out var primary) &&
                    primary.ValueKind == JsonValueKind.String)
                {
                    return primary.GetString();
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static bool LooksLikeKey(string? value)
        => !string.IsNullOrWhiteSpace(value) && KeyResponseParser.LooksLikeKeyMaterial(value);
}
