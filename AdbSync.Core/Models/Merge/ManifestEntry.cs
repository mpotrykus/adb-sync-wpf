namespace AdbSync.Core.Models.Merge;

/// <summary>The last known state of a file, as seen simultaneously in both staging and master right after a successful merge.</summary>
public sealed record ManifestEntry(long Size, DateTimeOffset ModifiedUtc);
