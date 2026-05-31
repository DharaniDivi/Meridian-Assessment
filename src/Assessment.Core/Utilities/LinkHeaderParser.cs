using System.Text.Json;
using System.Text.RegularExpressions;

namespace Assessment.Core.Utilities;

public static partial class LinkHeaderParser
{
    [GeneratedRegex(@"<(?<url>[^>]+)>;\s*rel=""?(?<rel>[^"";,]+)""?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LinkRegex();

    public static IReadOnlyDictionary<string, string> Parse(string? linkHeader)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(linkHeader))
        {
            return result;
        }

        foreach (Match match in LinkRegex().Matches(linkHeader))
        {
            var rel = match.Groups["rel"].Value.Trim();
            var url = match.Groups["url"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(rel) && !string.IsNullOrWhiteSpace(url))
            {
                result[rel] = url;
            }
        }

        return result;
    }

    public static string? GetBatchPath(string? linkHeader)
    {
        var links = Parse(linkHeader);
        if (links.TryGetValue("batch", out var batch))
        {
            return NormalizePath(batch);
        }

        return links.Values.Select(NormalizePath).FirstOrDefault();
    }

    public static IReadOnlyList<string> GetPathsByRel(string? linkHeader, params string[] relNames)
    {
        var links = Parse(linkHeader);
        var paths = new List<string>();
        foreach (var rel in relNames)
        {
            if (links.TryGetValue(rel, out var url))
            {
                paths.Add(NormalizePath(url));
            }
        }

        return paths;
    }

    public static IReadOnlyList<string> GetAllPaths(string? linkHeader)
        => Parse(linkHeader).Values.Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static (int Start, int End)? ParseRangeFromPath(string path)
    {
        var match = Regex.Match(path, @"range=(\d+)-(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
    }

    public static string BuildBatchPath(int start, int end)
        => $"api/v1/dataset?batch=true&range={start}-{end}";

    private static string NormalizePath(string url)
    {
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(url);
            return uri.PathAndQuery.TrimStart('/');
        }

        return url.TrimStart('/');
    }
}
