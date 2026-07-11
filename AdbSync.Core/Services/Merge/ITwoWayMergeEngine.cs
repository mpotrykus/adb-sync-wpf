using AdbSync.Core.Models.Merge;

namespace AdbSync.Core.Services.Merge;

public interface ITwoWayMergeEngine
{
    Task<MergeResult> MergeAsync(string stagingPath, string masterPath, SyncManifest manifest, MergeOptions options, CancellationToken ct = default);
}
