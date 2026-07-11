using AdbSync.Core.Models.Merge;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Models.Transfer;
using AdbSync.Core.Services.Transfer;

namespace AdbSync.Core.Services.Merge;

public sealed class ManifestStore(AppPaths paths) : IManifestStore
{
    private static readonly IExcludeMatcher NoExclude = new ExcludeMatcher([]);
    private static readonly TimeSpan ModifiedTolerance = TimeSpan.FromSeconds(2);

    public async Task<SyncManifest> GetOrBootstrapAsync(string jobName, string deviceName, string stagingPath, string masterPath, CancellationToken ct = default)
    {
        var existing = await JsonFileIo.ReadAsync(GetManifestPath(jobName, deviceName), AppConfigJsonContext.Default.SyncManifest, ct);
        if (existing is not null)
            return existing;

        var staging = LocalFileTreeScanner.Scan(stagingPath, NoExclude).Where(e => !e.IsDirectory);
        var masterByPath = LocalFileTreeScanner.Scan(masterPath, NoExclude)
            .Where(e => !e.IsDirectory)
            .ToDictionary(e => e.RelativePath, StringComparer.Ordinal);

        var entries = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        foreach (var s in staging)
        {
            if (masterByPath.TryGetValue(s.RelativePath, out var m) && IsSame(s, m))
                entries[s.RelativePath] = new ManifestEntry(s.Size, s.ModifiedUtc);
        }

        return new SyncManifest { Version = 1, LastMergedAtUtc = DateTimeOffset.UtcNow, Entries = entries };
    }

    public Task SaveAsync(string jobName, string deviceName, SyncManifest manifest, CancellationToken ct = default) =>
        JsonFileIo.WriteAtomicAsync(GetManifestPath(jobName, deviceName), manifest, AppConfigJsonContext.Default.SyncManifest, ct);

    private string GetManifestPath(string jobName, string deviceName) =>
        Path.Combine(paths.ManifestsDir, jobName, $"{deviceName}.manifest.json");

    private static bool IsSame(FileEntry a, FileEntry b) =>
        a.Size == b.Size && (a.ModifiedUtc - b.ModifiedUtc).Duration() <= ModifiedTolerance;
}
