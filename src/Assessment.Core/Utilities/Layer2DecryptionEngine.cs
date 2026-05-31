using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sodium;

namespace Assessment.Core.Utilities;

public static class Layer2DecryptionEngine
{
    public static bool TryDecryptToJsonObject(string ciphertext, string keyMaterial, out string plaintext)
    {
        plaintext = string.Empty;
        foreach (var key in DeriveKeyVariants(keyMaterial))
        {
            foreach (var attempt in AttemptDecryptions(ciphertext, key))
            {
                if (IsJsonObject(attempt))
                {
                    plaintext = attempt;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> AttemptDecryptions(string ciphertext, byte[] key)
    {
        byte[] cipherBytes;
        try
        {
            cipherBytes = Convert.FromBase64String(ciphertext.Trim());
        }
        catch
        {
            yield break;
        }

        var rawUtf8 = Encoding.UTF8.GetString(cipherBytes);
        if (rawUtf8.TrimStart().StartsWith('{'))
        {
            yield return rawUtf8;
        }

        if (TrySecretBox(cipherBytes, key, out var secretBox))
        {
            yield return secretBox;
        }

        if (TryChaCha20Poly1305(cipherBytes, key, 12, out var chacha12))
        {
            yield return chacha12;
        }

        if (TryChaCha20Poly1305(cipherBytes, key, 8, out var chacha8))
        {
            yield return chacha8;
        }

        if (TryAesGcm(cipherBytes, key, 12, out var gcm12))
        {
            yield return gcm12;
        }

        if (TryAesGcm(cipherBytes, key, 16, out var gcm16))
        {
            yield return gcm16;
        }

        if (TryAesGcmTagFirst(cipherBytes, key, 12, out var gcmTagFirst))
        {
            yield return gcmTagFirst;
        }

        if (TryAesCbc(cipherBytes, key, 16, out var cbc16))
        {
            yield return cbc16;
        }

        if (TryAesCbc(cipherBytes, key, 0, out var cbcNoIv))
        {
            yield return cbcNoIv;
        }

        if (TryAesEcb(cipherBytes, key, out var ecb))
        {
            yield return ecb;
        }
    }

    internal static IEnumerable<byte[]> DeriveKeyVariants(string keyMaterial)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keys = new List<byte[]>();
        void AddKey(byte[] key)
        {
            var id = Convert.ToHexString(key);
            if (seen.Add(id))
            {
                keys.Add(key);
            }
        }

        var trimmed = keyMaterial.Trim();
        AddKey(NormalizeAesKey(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed))));

        if (trimmed.StartsWith("sa_", StringComparison.OrdinalIgnoreCase))
        {
            var body = trimmed["sa_".Length..];
            AddKey(NormalizeAesKey(SHA256.HashData(Encoding.UTF8.GetBytes(body))));
            if (IsHex(body) && body.Length == 64)
            {
                AddKey(NormalizeAesKey(Convert.FromHexString(body)));
            }
        }

        if (IsHex(trimmed) && trimmed.Length is 64 or 32)
        {
            AddKey(NormalizeAesKey(Convert.FromHexString(trimmed.Length == 64 ? trimmed : trimmed.PadRight(64, '0'))));
        }

        if (Guid.TryParse(trimmed, out var guid))
        {
            AddKey(NormalizeAesKey(guid.ToByteArray()));
        }

        try
        {
            var decoded = Convert.FromBase64String(trimmed);
            if (decoded.Length is 16 or 24 or 32)
            {
                AddKey(decoded);
            }
        }
        catch
        {
            // ignore
        }

        return keys;
    }

    internal static IEnumerable<(string Key, string Source)> DeriveHmacKeyCandidates(
        string apiKey,
        string contentHash,
        string? submissionId)
    {
        var results = new List<(string Key, string Source)>();
        void Add(byte[] keyBytes, string source)
        {
            results.Add((Convert.ToHexString(keyBytes), source));
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return results;
        }

        var apiKeyBody = apiKey.StartsWith("sa_", StringComparison.OrdinalIgnoreCase)
            ? apiKey["sa_".Length..]
            : apiKey;
        var apiKeyBytes = IsHex(apiKeyBody) && apiKeyBody.Length == 64
            ? Convert.FromHexString(apiKeyBody)
            : SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        var contentHashBytes = IsHex(contentHash) && contentHash.Length == 64
            ? Convert.FromHexString(contentHash)
            : SHA256.HashData(Encoding.UTF8.GetBytes(contentHash));

        Add(HMACSHA256.HashData(apiKeyBytes, contentHashBytes), "HMAC(apiKeyBody, contentHash)");
        Add(HMACSHA256.HashData(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)), contentHashBytes), "HMAC(SHA256(apiKey), contentHash)");
        Add(HMACSHA256.HashData(contentHashBytes, apiKeyBytes), "HMAC(contentHash, apiKeyBody)");

        if (!string.IsNullOrWhiteSpace(submissionId) && Guid.TryParse(submissionId, out var guid))
        {
            var guidBytes = guid.ToByteArray();
            Add(HMACSHA256.HashData(apiKeyBytes, guidBytes), "HMAC(apiKeyBody, submission_id)");
            Add(HMACSHA256.HashData(contentHashBytes, guidBytes), "HMAC(contentHash, submission_id)");
        }

        return results;
    }

    private static bool TrySecretBox(byte[] cipherBytes, byte[] key, out string plaintext)
    {
        plaintext = string.Empty;
        const int nonceSize = 24;
        const int macSize = 16;
        if (cipherBytes.Length < nonceSize + macSize + 1)
        {
            return false;
        }

        try
        {
            var boxKey = NormalizeAesKey(key);
            var nonce = cipherBytes.AsSpan(0, nonceSize).ToArray();
            var box = cipherBytes.AsSpan(nonceSize).ToArray();

            byte[] plain;
            try
            {
                plain = SecretBox.Open(box, nonce, boxKey);
            }
            catch
            {
                if (box.Length <= macSize)
                {
                    return false;
                }

                var mac = box.AsSpan(0, macSize).ToArray();
                var cipher = box.AsSpan(macSize).ToArray();
                plain = SecretBox.OpenDetached(cipher, mac, nonce, boxKey);
            }

            plaintext = Encoding.UTF8.GetString(plain);
            return plain.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryChaCha20Poly1305(byte[] cipherBytes, byte[] key, int nonceSize, out string plaintext)
    {
        plaintext = string.Empty;
        var minSize = nonceSize + 16 + 1;
        if (cipherBytes.Length < minSize)
        {
            return false;
        }

        try
        {
            var nonce = cipherBytes.AsSpan(0, nonceSize);
            var tag = cipherBytes.AsSpan(cipherBytes.Length - 16, 16);
            var cipher = cipherBytes.AsSpan(nonceSize, cipherBytes.Length - nonceSize - 16);
            var output = new byte[cipher.Length];
            using var chacha = new ChaCha20Poly1305(NormalizeAesKey(key));
            chacha.Decrypt(nonce, cipher, tag, output);
            plaintext = Encoding.UTF8.GetString(output);
            return output.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryAesGcm(byte[] cipherBytes, byte[] key, int nonceSize, out string plaintext)
    {
        plaintext = string.Empty;
        var minSize = nonceSize + 16 + 1;
        if (cipherBytes.Length < minSize)
        {
            return false;
        }

        try
        {
            var nonce = cipherBytes.AsSpan(0, nonceSize);
            var tag = cipherBytes.AsSpan(cipherBytes.Length - 16, 16);
            var cipher = cipherBytes.AsSpan(nonceSize, cipherBytes.Length - nonceSize - 16);
            var output = new byte[cipher.Length];
            using var aes = new AesGcm(NormalizeAesKey(key), 16);
            aes.Decrypt(nonce, cipher, tag, output);
            plaintext = Encoding.UTF8.GetString(output);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryAesGcmTagFirst(byte[] cipherBytes, byte[] key, int nonceSize, out string plaintext)
    {
        plaintext = string.Empty;
        var minSize = 16 + nonceSize + 1;
        if (cipherBytes.Length < minSize)
        {
            return false;
        }

        try
        {
            var tag = cipherBytes.AsSpan(0, 16);
            var nonce = cipherBytes.AsSpan(16, nonceSize);
            var cipher = cipherBytes.AsSpan(16 + nonceSize);
            var output = new byte[cipher.Length];
            using var aes = new AesGcm(NormalizeAesKey(key), 16);
            aes.Decrypt(nonce, cipher, tag, output);
            plaintext = Encoding.UTF8.GetString(output);
            return output.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryAesCbc(byte[] cipherBytes, byte[] key, int ivSize, out string plaintext)
    {
        plaintext = string.Empty;
        if (cipherBytes.Length <= ivSize || (cipherBytes.Length - ivSize) % 16 != 0)
        {
            return false;
        }

        try
        {
            var iv = ivSize == 0 ? new byte[16] : cipherBytes.AsSpan(0, ivSize).ToArray();
            var cipher = ivSize == 0 ? cipherBytes : cipherBytes.AsSpan(ivSize).ToArray();
            using var aes = Aes.Create();
            aes.Key = NormalizeAesKey(key);
            aes.IV = ivSize == 0 ? new byte[16] : iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            var output = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
            plaintext = Encoding.UTF8.GetString(output);
            return output.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryAesEcb(byte[] cipherBytes, byte[] key, out string plaintext)
    {
        plaintext = string.Empty;
        if (cipherBytes.Length == 0 || cipherBytes.Length % 16 != 0)
        {
            return false;
        }

        try
        {
            using var aes = Aes.Create();
            aes.Key = NormalizeAesKey(key);
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            var output = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            plaintext = Encoding.UTF8.GetString(output);
            return output.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] NormalizeAesKey(byte[] key) => key.Length switch
    {
        16 or 24 or 32 => key,
        _ => SHA256.HashData(key)
    };

    private static bool IsHex(string value)
        => value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}
