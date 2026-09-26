using System.Security.Cryptography;

namespace WClop.Core.Storage;

public static class ContentHash
{
    /// <summary>Lowercase hex SHA-256 of the file's contents.</summary>
    public static async Task<string> OfFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static string OfBytes(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
