using System.Security.Cryptography;

namespace Kakitome.Application.Library;

public static class ContentHash
{
    public static string Sha256Hex(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
