using System.Text.Json;

namespace Assessment.Core.Utilities;

public static class SubmissionResponseParser
{
    public sealed record ParsedSubmission(
        bool? Correct,
        int? Layer,
        string? Message,
        string? Key,
        string RawBody);

    public static ParsedSubmission? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool? correct = root.TryGetProperty("correct", out var c)
                ? c.ValueKind == JsonValueKind.True
                : null;

            int? layer = root.TryGetProperty("layer", out var l) && l.TryGetInt32(out var layerValue)
                ? layerValue
                : null;

            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            var key = KeyResponseParser.ExtractKeyFromElement(root);

            return new ParsedSubmission(correct, layer, message, key, body);
        }
        catch
        {
            return null;
        }
    }
}
