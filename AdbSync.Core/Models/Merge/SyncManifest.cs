namespace AdbSync.Core.Models.Merge;

public sealed class SyncManifest
{
    public int Version { get; set; } = 1;
    public DateTimeOffset LastMergedAtUtc { get; set; }
    public Dictionary<string, ManifestEntry> Entries { get; set; } = [];

    /// <summary>When this device's whole folder was last pulled and merged without skipping anything. Null until then,
    /// which makes the next incremental-pull run do a full one first.</summary>
    public DateTimeOffset? LastFullPullAtUtc { get; set; }
}
