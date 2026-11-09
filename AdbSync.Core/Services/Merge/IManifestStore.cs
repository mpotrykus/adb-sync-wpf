using AdbSync.Core.Models.Merge;

namespace AdbSync.Core.Services.Merge;

public interface IManifestStore
{
    /// <summary>
    /// Loads the persisted manifest for (jobName, deviceName), or - if none exists yet - bootstraps one from the
    /// current intersection of staging and master (files identical in both by size+mtime become the baseline;
    /// anything only on one side is simply treated as new on its first merge).
    /// </summary>
    Task<SyncManifest> GetOrBootstrapAsync(string jobName, string deviceName, string stagingPath, string masterPath, CancellationToken ct = default);

    /// <summary>The persisted manifest for (jobName, deviceName), or null if none has been saved yet.</summary>
    Task<SyncManifest?> TryGetAsync(string jobName, string deviceName, CancellationToken ct = default);

    Task SaveAsync(string jobName, string deviceName, SyncManifest manifest, CancellationToken ct = default);

    /// <summary>Discards every device manifest for the job. Returns how many were removed.</summary>
    int Clear(string jobName);

    /// <summary>True when at least one persisted manifest for the job records files.</summary>
    Task<bool> AnyEntriesAsync(string jobName, CancellationToken ct = default);
}
