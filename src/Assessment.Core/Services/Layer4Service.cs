using Assessment.Core.Models;
using Microsoft.Extensions.Logging;

namespace Assessment.Core.Services;

public sealed class Layer4Service
{
    private readonly Layer2Service _layer2;
    private readonly ILogger<Layer4Service> _logger;

    public Layer4Service(Layer2Service layer2, ILogger<Layer4Service> logger)
    {
        _layer2 = layer2;
        _logger = logger;
    }

    public async Task<Layer4Result> GenerateAnalysisAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var records = await _layer2.LoadDecryptedRecordsAsync(cancellationToken);
            if (records.Count == 0)
            {
                return new Layer4Result(false, string.Empty, "No decrypted records found. Run Layer 2 first.");
            }

            var fieldNames = records
                .SelectMany(r => r.Fields.Keys)
                .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .ToList();

            var stringFieldLengths = records
                .SelectMany(r => r.Fields.Values.OfType<string>())
                .GroupBy(s => s.Length)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => $"length {g.Key}: {g.Count()} values")
                .ToList();

            var analysis = $"""
                Dataset overview ({records.Count} records):

                - Distinct field names ({fieldNames.Count}): {string.Join(", ", fieldNames.Take(15))}
                - Most common string lengths: {string.Join("; ", stringFieldLengths)}
                - Record index range: 0..{records.Count - 1}

                Observations:
                1. Field cardinality suggests structured payloads rather than free text.
                2. Repeated patterns across records may encode the Layer 3 answer in a non-obvious field.
                3. Recommend cross-field correlation (e.g. first characters, sorted unique tokens).

                This analysis was generated automatically; refine after inspecting decrypted.jsonl.
                """;

            _logger.LogInformation("Layer 4: generated analysis for {Count} records", records.Count);
            return new Layer4Result(true, analysis, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Layer 4 failed");
            return new Layer4Result(false, string.Empty, ex.Message);
        }
    }
}
