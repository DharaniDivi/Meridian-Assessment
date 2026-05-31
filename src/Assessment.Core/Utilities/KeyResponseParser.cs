using System.Text.Json;

namespace Assessment.Core.Utilities;

public static class KeyResponseParser
{
    public static string? ExtractKeyFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return ExtractKeyFromElement(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    public static bool LooksLikeKeyMaterial(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Contains(' ', StringComparison.Ordinal) && !trimmed.StartsWith('{'))
        {
            return false;
        }

        if (trimmed.Length is >= 32 and <= 512 &&
            trimmed.All(static c => char.IsLetterOrDigit(c) || "+/=-_".Contains(c)))
        {
            return true;
        }

        return false;
    }

    internal static string? ExtractKeyFromElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[]
        {
            "decryption_key", "decryptionKey", "layer2_key", "layer2Key",
            "data_key", "dataKey", "aes_key", "aesKey", "key", "secret",
            "token", "value", "credential", "credentials", "unlock",
            "access_key", "accessKey", "api_key", "apiKey"
        })
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }

        if (element.TryGetProperty("data", out var data))
        {
            var nested = ExtractKeyFromElement(data);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    public static string? FindKeyInHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        foreach (var header in headers)
        {
            if (!header.Key.Contains("key", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.Contains("secret", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.Contains("token", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.Contains("credential", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                if (LooksLikeKeyMaterial(value))
                {
                    return value.Trim();
                }

                var extracted = ExtractKeyFromJson(value);
                if (LooksLikeKeyMaterial(extracted))
                {
                    return extracted;
                }
            }
        }

        return null;
    }
}
