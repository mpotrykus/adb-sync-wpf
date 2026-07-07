namespace AdbSync.Core.Merge;

public interface IManifestStore
{
    /// <summary>
    /// Loads the persisted manifest for (jobName, deviceName), or - if none exists yet - bootstraps one from the
    /// current intersection of staging and master (files identical in both by size+mtime become the baseline;
    /// anything only on one side is simply treated as new on its first merge).
    /// </summary>
    Task<SyncManifest> GetOrBootstrapAsync(string jobName, string deviceName, string stagingPath, string masterPath, CancellationToken ct = default);

    Task SaveAsync(string jobName, string deviceName, SyncManifest manifest, CancellationToken ct = default);
}
