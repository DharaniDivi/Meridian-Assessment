namespace Assessment.Core.Models;

public sealed record ErrorEnvelope(string Error, int? RetryAfter = null, IReadOnlyList<string>? ValidValues = null);

public sealed record SubmissionRequest(string Type, string Value, string? Notes = null);

public sealed record SubmissionResult(bool Success, int StatusCode, string? Message, IReadOnlyList<string>? ValidTypes = null);

public sealed record TimeRemainingResult(bool Success, int StatusCode, TimeSpan? Remaining, string? Message);

public sealed record HealthResult(bool IsHealthy, int StatusCode, string? Body);

public sealed record Layer1Result(
    bool Success,
    string? HashHex,
    long ByteCount,
    string? Message,
    IReadOnlyDictionary<string, string>? Hashes = null);

public sealed record Layer2Result(bool Success, int RecordCount, string? HashHex, string? Message);

public sealed record Layer3Result(bool Success, string? Answer, string? Message);

public sealed record Layer4Result(bool Success, string Analysis, string? Message);

public sealed record DecryptedRecord(int Index, string RawJson, Dictionary<string, object?> Fields);

public sealed record DatasetMetadata(string? Etag, string? ContentType, long ByteCount);
