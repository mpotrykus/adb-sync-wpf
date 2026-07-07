namespace AdbSync.Core.Merge;

public interface ITwoWayMergeEngine
{
    Task<MergeResult> MergeAsync(string stagingPath, string masterPath, SyncManifest manifest, MergeOptions options, CancellationToken ct = default);
}
