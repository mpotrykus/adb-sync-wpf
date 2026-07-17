using System.Security.Cryptography;

namespace AdbSync.Core.Services.Common;

/// <summary>
/// Tiebreaker for the rare case where two files already agree on size and mtime (within tolerance) but that
/// still isn't proof they're the same bytes. Only called once size/mtime already look equal, so the cost of
/// reading both files stays confined to that narrow, infrequent case rather than every comparison.
/// </summary>
internal static class ContentHasher
{
    public static bool FilesAreIdentical(string pathA, string pathB)
    {
        using var streamA = File.OpenRead(pathA);
        using var streamB = File.OpenRead(pathB);
        return SHA256.HashData(streamA).AsSpan().SequenceEqual(SHA256.HashData(streamB));
    }
}
