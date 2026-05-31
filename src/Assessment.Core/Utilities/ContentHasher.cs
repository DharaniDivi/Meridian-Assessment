using System.Security.Cryptography;

namespace Assessment.Core.Utilities;

public static class ContentHasher
{
    public static string Sha256Hex(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public static async Task<string> Sha256HexAsync(Stream content, CancellationToken cancellationToken = default)
    {
        var hash = await SHA256.HashDataAsync(content, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
