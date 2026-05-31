using System.Globalization;
using System.Text.Json;

namespace Assessment.Core.Utilities;

public static class TimeResponseParser
{
    public static TimeSpan? ParseRemaining(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return ParseRemaining(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    public static TimeSpan? ParseRemaining(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Number && root.TryGetInt64(out var directSeconds))
        {
            return TimeSpan.FromSeconds(directSeconds);
        }

        if (root.ValueKind == JsonValueKind.String &&
            TimeSpan.TryParse(root.GetString(), CultureInfo.InvariantCulture, out var directSpan))
        {
            return directSpan;
        }

        foreach (var name in new[]
        {
            "remaining_seconds", "remainingSeconds", "seconds_remaining", "secondsRemaining",
            "seconds", "remaining", "time_remaining", "timeRemaining"
        })
        {
            if (!root.TryGetProperty(name, out var prop))
            {
                continue;
            }

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }

            if (prop.ValueKind == JsonValueKind.String)
            {
                var text = prop.GetString();
                if (long.TryParse(text, out var parsedSeconds))
                {
                    return TimeSpan.FromSeconds(parsedSeconds);
                }

                if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span))
                {
                    return span;
                }
            }
        }

        foreach (var name in new[] { "expires_at", "expiresAt", "deadline", "ends_at", "endsAt" })
        {
            if (root.TryGetProperty(name, out var prop) &&
                prop.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(prop.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires))
            {
                var remaining = expires - DateTimeOffset.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        if (root.TryGetProperty("data", out var data))
        {
            return ParseRemaining(data);
        }

        return null;
    }

    public static TimeSpan? ParseRemainingFromHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        foreach (var header in headers)
        {
            if (!header.Key.Contains("remain", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.Contains("time", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = header.Value.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (long.TryParse(value, out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }

            if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span))
            {
                return span;
            }
        }

        return null;
    }
}
