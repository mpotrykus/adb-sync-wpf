namespace AdbSync.Core.Models.Transfer;

/// <summary>One file/directory in a tree being mirrored, keyed by path relative to that tree's root.</summary>
public sealed record FileEntry(string RelativePath, bool IsDirectory, long Size, DateTimeOffset ModifiedUtc);
