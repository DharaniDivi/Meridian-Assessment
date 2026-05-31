using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assessment.Core.Configuration;
using Assessment.Core.Models;
using Assessment.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Assessment.Core.Http;

public sealed class AssessmentHttpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly AssessmentOptions _options;
    private readonly ILogger<AssessmentHttpClient> _logger;

    public AssessmentHttpClient(
        HttpClient httpClient,
        IOptions<AssessmentOptions> options,
        ILogger<AssessmentHttpClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        }

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    public async Task<HealthResult> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            () => _httpClient.GetAsync("api/v1/health", cancellationToken),
            consumeRateLimit: false,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new HealthResult(response.IsSuccessStatusCode, (int)response.StatusCode, body);
    }

    private TimeSpan? _headerTimeHint;
    private DateTimeOffset _headerTimeHintCapturedAt;

    public async Task<TimeRemainingResult> GetTimeRemainingAsync(CancellationToken cancellationToken = default)
    {
        string[] paths =
        [
            "api/v1/time",
            "api/v1/time-remaining",
            "api/v1/remaining-time",
            "api/v1/clock",
            "api/v1/session/time",
            "api/v1/candidate/time",
            "api/v1/assessment/time",
            "api/v1/window"
        ];

        string? lastError = null;
        var lastStatus = 404;

        foreach (var path in paths)
        {
            var response = await SendAsync(
                () => _httpClient.GetAsync(path, cancellationToken),
                consumeRateLimit: false,
                cancellationToken);

            CaptureTimeHintFromHeaders(response);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                lastStatus = (int)response.StatusCode;
                lastError = TryParseError(body)?.Error ?? body;
                if (string.IsNullOrWhiteSpace(lastError))
                {
                    lastError = $"HTTP {lastStatus} from {path}";
                }

                continue;
            }

            var remaining = TimeResponseParser.ParseRemaining(body);
            if (remaining.HasValue)
            {
                return new TimeRemainingResult(true, (int)response.StatusCode, remaining, $"source: GET {path}");
            }

            lastStatus = (int)response.StatusCode;
            lastError = $"Unexpected time response from {path}: {body}";
        }

        if (_headerTimeHint.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - _headerTimeHintCapturedAt;
            var adjusted = _headerTimeHint.Value - elapsed;
            if (adjusted < TimeSpan.Zero)
            {
                adjusted = TimeSpan.Zero;
            }

            return new TimeRemainingResult(true, 200, adjusted, "source: response headers");
        }

        var message = lastStatus == 404
            ? "Time endpoint not found on platform. Track your 3-hour window manually from your first authenticated call."
            : lastError ?? "Unable to read remaining time.";

        return new TimeRemainingResult(false, lastStatus, null, message);
    }

    public async Task<(HttpResponseMessage Response, Stream Stream)> GetDatasetStreamAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            () => _httpClient.GetAsync("api/v1/dataset", HttpCompletionOption.ResponseHeadersRead, cancellationToken),
            consumeRateLimit: true,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancellationToken);
            throw new AssessmentApiException((int)response.StatusCode, error);
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return (response, stream);
    }

    public async Task<(HttpResponseMessage Response, Stream Stream)> GetPathStreamAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var path = relativePath.TrimStart('/');
        var response = await SendAsync(
            () => _httpClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken),
            consumeRateLimit: true,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancellationToken);
            throw new AssessmentApiException((int)response.StatusCode, error);
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return (response, stream);
    }

    public async Task<DatasetStats?> GetDatasetStatsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            () => _httpClient.GetAsync("api/v1/stats", cancellationToken),
            consumeRateLimit: true,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseDatasetStats(body);
    }

    internal static DatasetStats? ParseDatasetStats(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            int? total = null;
            foreach (var name in new[]
                     {
                         "total_records", "totalRecords", "dataset_records", "datasetRecords",
                         "record_count", "recordCount", "total", "count", "records"
                     })
            {
                if (TryReadInt(root, name, out var value))
                {
                    total = value;
                    break;
                }
            }

            if (!total.HasValue && root.TryGetProperty("dataset", out var dataset))
            {
                foreach (var name in new[]
                         {
                             "total_records", "totalRecords", "dataset_records", "datasetRecords",
                             "record_count", "recordCount", "total", "count", "records"
                         })
                {
                    if (TryReadInt(dataset, name, out var value))
                    {
                        total = value;
                        break;
                    }
                }
            }

            var batchSize = 100;
            foreach (var name in new[] { "batch_size", "batchSize", "page_size", "pageSize", "limit" })
            {
                if (TryReadInt(root, name, out var value) && value > 0)
                {
                    batchSize = value;
                    break;
                }
            }

            return new DatasetStats(total, batchSize, body);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return false;
        }

        return prop.TryGetInt32(out value);
    }

    public async Task<(string? Key, string? Error, int? StatusCode)> TryGetKeyFromPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            () => _httpClient.GetAsync(path, cancellationToken),
            consumeRateLimit: true,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = TryParseError(body)?.Error ?? body;
            if (string.IsNullOrWhiteSpace(error))
            {
                error = $"HTTP {(int)response.StatusCode} from {path}";
            }

            return (null, error, (int)response.StatusCode);
        }

        var headerKey = KeyResponseParser.FindKeyInHeaders(
            response.Headers.Concat(response.Content.Headers)
                .Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value)));
        if (!string.IsNullOrWhiteSpace(headerKey) && KeyResponseParser.LooksLikeKeyMaterial(headerKey))
        {
            return (headerKey, null, (int)response.StatusCode);
        }

        var key = KeyResponseParser.ExtractKeyFromJson(body);
        if (key is null && KeyResponseParser.LooksLikeKeyMaterial(body.Trim()))
        {
            key = body.Trim().Trim('"');
        }

        return (key, key is null ? $"Unexpected key response shape from {path}" : null, (int)response.StatusCode);
    }

    public async Task<(HttpResponseMessage Response, string Body)> TryGetBodyFromPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            () => _httpClient.GetAsync(path, cancellationToken),
            consumeRateLimit: true,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response, body);
    }

    public async Task SaveDatasetHeadersAsync(
        HttpResponseMessage response,
        string dataDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(dataDirectory);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (header.Key.Contains("key", StringComparison.OrdinalIgnoreCase) ||
                header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Link", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("ETag", StringComparison.OrdinalIgnoreCase))
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
        }

        var json = JsonSerializer.Serialize(headers, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "dataset-headers.json"), json, cancellationToken);
    }

    public async Task<string> GetDecryptionKeyAsync(CancellationToken cancellationToken = default)
    {
        var (key, error, _) = await TryGetKeyFromPathAsync("api/v1/key", cancellationToken);
        if (key is not null)
        {
            return key;
        }

        throw new AssessmentApiException(404, error ?? "Decryption key endpoint not available.");
    }

    public async Task<SubmissionResult> SubmitAsync(
        SubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(request, JsonOptions);
        _logger.LogDebug("Submit payload: {Json}", json);

        var response = await SendAsync(
            () => _httpClient.PostAsync(
                "api/v1/submit",
                new StringContent(json, Encoding.UTF8, "application/json"),
                cancellationToken),
            consumeRateLimit: true,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        await SaveSubmitResponseHeadersAsync(response, request.Type, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var parsed = SubmissionResponseParser.Parse(body);
            if (parsed?.Correct == false)
            {
                return new SubmissionResult(
                    false,
                    (int)response.StatusCode,
                    body);
            }

            return new SubmissionResult(true, (int)response.StatusCode, body);
        }

        var envelope = TryParseError(body);
        var error = envelope?.Error ?? body;
        if (string.IsNullOrWhiteSpace(error))
        {
            error = $"HTTP {(int)response.StatusCode}: {body}";
        }

        return new SubmissionResult(
            false,
            (int)response.StatusCode,
            error,
            envelope?.ValidValues?.ToList());
    }

    public async Task<IReadOnlyList<string>> DiscoverSubmissionTypesAsync(CancellationToken cancellationToken = default)
    {
        var result = await SubmitAsync(new SubmissionRequest("__invalid__", "probe"), cancellationToken);
        return result.ValidTypes ?? Array.Empty<string>();
    }

    public async Task<IReadOnlyList<string>> DiscoverEndpointsAsync(CancellationToken cancellationToken = default)
    {
        var candidates = new[]
        {
            "api/v1/health",
            "api/v1/time",
            "api/v1/dataset",
            "api/v1/key",
            "api/v1/submit"
        };

        var results = new List<string>();
        foreach (var path in candidates)
        {
            try
            {
                var response = await _httpClient.SendAsync(
                    new HttpRequestMessage(HttpMethod.Options, path),
                    cancellationToken);
                results.Add($"{path}: {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                results.Add($"{path}: error ({ex.Message})");
            }
        }

        return results;
    }

    private async Task SaveSubmitResponseHeadersAsync(
        HttpResponseMessage response,
        string submissionType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.DataDirectory))
        {
            return;
        }

        Directory.CreateDirectory(_options.DataDirectory);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        var safeType = submissionType.Replace('/', '-').Replace('\\', '-');
        var json = JsonSerializer.Serialize(headers, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(
            Path.Combine(_options.DataDirectory, $"submission-{safeType}-headers.json"),
            json,
            cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<Task<HttpResponseMessage>> send,
        bool consumeRateLimit,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            var response = await send();
            LogRateLimitHeaders(response, consumeRateLimit);
            CaptureTimeHintFromHeaders(response);

            if (!ShouldRetryStatus(response.StatusCode) || attempt >= _options.MaxRetries)
            {
                return response;
            }

            var retryAfter = GetRetryAfterSeconds(response);
            if (retryAfter <= 0)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                retryAfter = TryParseError(body)?.RetryAfter ?? Math.Min(30, (int)Math.Pow(2, attempt));
            }

            _logger.LogWarning(
                "Transient HTTP {Status}. Retry {Attempt}/{Max} after {Seconds}s",
                (int)response.StatusCode,
                attempt,
                _options.MaxRetries,
                retryAfter);

            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(retryAfter), cancellationToken);
        }
    }

    private static bool ShouldRetryStatus(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.BadGateway
            or HttpStatusCode.GatewayTimeout;

    private void CaptureTimeHintFromHeaders(HttpResponseMessage response)
    {
        var headers = response.Headers
            .Concat(response.Content.Headers)
            .Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value));

        var remaining = TimeResponseParser.ParseRemainingFromHeaders(headers);
        if (!remaining.HasValue)
        {
            return;
        }

        _headerTimeHint = remaining;
        _headerTimeHintCapturedAt = DateTimeOffset.UtcNow;
    }

    private void LogRateLimitHeaders(HttpResponseMessage response, bool consumeRateLimit)
    {
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (header.Key.StartsWith("RateLimit", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("{Header}: {Value} (consumes={Consumes})",
                    header.Key, string.Join(", ", header.Value), consumeRateLimit);
            }
        }
    }

    private static int GetRetryAfterSeconds(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return (int)Math.Ceiling(delta.TotalSeconds);
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            return Math.Max(1, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds));
        }

        if (response.Content.Headers.TryGetValues("Retry-After", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var seconds))
        {
            return seconds;
        }

        return 0;
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return TryParseError(body)?.Error ?? body;
    }

    private static ErrorEnvelope? TryParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("error", out var errorElement))
            {
                return null;
            }

            int? retryAfter = null;
            if (root.TryGetProperty("retry_after", out var ra))
            {
                retryAfter = ra.GetInt32();
            }
            else if (root.TryGetProperty("retryAfter", out var ra2))
            {
                retryAfter = ra2.GetInt32();
            }

            IReadOnlyList<string>? validValues = null;
            if (root.TryGetProperty("valid_values", out var vv) && vv.ValueKind == JsonValueKind.Array)
            {
                validValues = vv.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
            }
            else if (root.TryGetProperty("validValues", out var vv2) && vv2.ValueKind == JsonValueKind.Array)
            {
                validValues = vv2.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
            }
            else if (root.TryGetProperty("valid_types", out var vt) && vt.ValueKind == JsonValueKind.Array)
            {
                validValues = vt.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
            }
            else if (root.TryGetProperty("validTypes", out var vt2) && vt2.ValueKind == JsonValueKind.Array)
            {
                validValues = vt2.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
            }

            return new ErrorEnvelope(errorElement.GetString() ?? body, retryAfter, validValues);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class AssessmentApiException : Exception
{
    public int StatusCode { get; }

    public AssessmentApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}
