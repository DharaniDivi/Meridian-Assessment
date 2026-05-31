using System.Text.RegularExpressions;
using Assessment.Core.Models;
using Microsoft.Extensions.Logging;

namespace Assessment.Core.Services;

public sealed partial class Layer3Service
{
    private static readonly Regex AlphabeticAnswerPattern = AlphabeticPattern();
    private static readonly string[] HintFieldNames =
    [
        "answer", "code", "token", "keyword", "flag", "secret", "algorithm", "hint", "word", "key"
    ];

    private readonly Layer2Service _layer2;
    private readonly ILogger<Layer3Service> _logger;

    public Layer3Service(Layer2Service layer2, ILogger<Layer3Service> logger)
    {
        _layer2 = layer2;
        _logger = logger;
    }

    public async Task<Layer3Result> FindHiddenAnswerAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var records = await _layer2.LoadDecryptedRecordsAsync(cancellationToken);
            if (records.Count == 0)
            {
                return new Layer3Result(false, null, "No decrypted records found. Run Layer 2 first.");
            }

            var candidates = ExtractCandidates(records);
            if (candidates.Count == 0)
            {
                return new Layer3Result(false, null, "No alphabetic candidates found in decrypted records.");
            }

            var ranked = RankCandidates(candidates, records);
            var best = ranked.First();
            _logger.LogInformation("Layer 3: top candidate={Answer}, total candidates={Count}", best, ranked.Count);
            return new Layer3Result(true, best, $"Found {ranked.Count} candidate(s). Top: {string.Join(", ", ranked.Take(5))}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Layer 3 failed");
            return new Layer3Result(false, null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> GetAllCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var records = await _layer2.LoadDecryptedRecordsAsync(cancellationToken);
        return RankCandidates(ExtractCandidates(records), records);
    }

    private static List<string> RankCandidates(List<string> candidates, IReadOnlyList<DecryptedRecord> records)
    {
        return candidates
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .OrderByDescending(c => HintFieldNames.Any(h => records.Any(r => r.Fields.ContainsKey(h) && string.Equals(r.Fields[h]?.ToString(), c, StringComparison.OrdinalIgnoreCase))) ? 1 : 0)
            .ThenBy(c => candidates.Count(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase)) == 1 ? 0 : 1)
            .ThenBy(c => c.Length)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ExtractCandidates(IReadOnlyList<DecryptedRecord> records)
    {
        var results = new List<string>();

        foreach (var record in records)
        {
            foreach (var (name, value) in record.Fields)
            {
                if (HintFieldNames.Contains(name, StringComparer.OrdinalIgnoreCase) && value is string hint && AlphabeticAnswerPattern.IsMatch(hint))
                {
                    results.Add(hint);
                }

                if (value is string text)
                {
                    results.AddRange(FindAlphabeticTokens(text));
                }
            }

            results.AddRange(FindAlphabeticTokens(record.RawJson));
        }

        var acrostic = string.Concat(records
            .Select(r => r.Fields.Values.OfType<string>().FirstOrDefault())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => char.ToUpperInvariant(s![0])));

        if (AlphabeticAnswerPattern.IsMatch(acrostic))
        {
            results.Add(acrostic);
        }

        return results
            .Where(s => AlphabeticAnswerPattern.IsMatch(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> FindAlphabeticTokens(string text)
        => AlphabeticAnswerPattern.Matches(text).Select(m => m.Value);

    [GeneratedRegex(@"^[A-Za-z]{3,20}$", RegexOptions.Compiled)]
    private static partial Regex AlphabeticPattern();
}
