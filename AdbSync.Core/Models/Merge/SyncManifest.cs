namespace AdbSync.Core.Models.Merge;

public sealed class SyncManifest
{
    public int Version { get; set; } = 1;
    public DateTimeOffset LastMergedAtUtc { get; set; }
    public Dictionary<string, ManifestEntry> Entries { get; set; } = [];
}
