namespace Assessment.Core.Models;

public sealed record DatasetStats(
    int? TotalRecords,
    int BatchSize,
    string? RawJson);
