using System.Text.RegularExpressions;

namespace Assessment.Core.Utilities;

public static partial class EtagParser
{
    [GeneratedRegex(@"^[Ww]/""(?<hash>[0-9a-fA-F]{64})""$", RegexOptions.Compiled)]
    private static partial Regex WeakSha256EtagRegex();

    [GeneratedRegex(@"^""(?<hash>[0-9a-fA-F]{64})""$", RegexOptions.Compiled)]
    private static partial Regex StrongSha256EtagRegex();

    /// <summary>
    /// Extracts a lowercase SHA-256 hex digest from an HTTP ETag or saved header value.
    /// </summary>
    public static string? ExtractSha256Hex(string? etag)
    {
        if (string.IsNullOrWhiteSpace(etag))
        {
            return null;
        }

        var trimmed = etag.Trim();
        var weakMatch = WeakSha256EtagRegex().Match(trimmed);
        if (weakMatch.Success)
        {
            return weakMatch.Groups["hash"].Value.ToLowerInvariant();
        }

        var strongMatch = StrongSha256EtagRegex().Match(trimmed);
        if (strongMatch.Success)
        {
            return strongMatch.Groups["hash"].Value.ToLowerInvariant();
        }

        if (trimmed.Length == 64 && trimmed.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
        {
            return trimmed.ToLowerInvariant();
        }

        return null;
    }
}
