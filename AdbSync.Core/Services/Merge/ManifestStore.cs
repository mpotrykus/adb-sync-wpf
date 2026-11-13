using AdbSync.Core.Models.Merge;
using AdbSync.Core.Models.Transfer;
using AdbSync.Core.Services.Common;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Persistence;
using AdbSync.Core.Services.Transfer;
using System.Text.Json;

namespace AdbSync.Core.Services.Merge;

public sealed class ManifestStore(EngineDatabase database) : IManifestStore
{
    private static readonly IExcludeMatcher NoExclude = new ExcludeMatcher([]);
    private static readonly TimeSpan ModifiedTolerance = TimeSpan.FromSeconds(2);

    public ManifestStore(AppPaths paths) : this(new EngineDatabase(paths)) { }

    public async Task<SyncManifest> GetOrBootstrapAsync(string jobName, string deviceName, string stagingPath, string masterPath, CancellationToken ct = default)
    {
        var existing = await TryGetAsync(jobName, deviceName, ct);
        if (existing is not null)
            return existing;

        var staging = LocalFileTreeScanner.Scan(stagingPath, NoExclude).Where(e => !e.IsDirectory);
        var masterByPath = LocalFileTreeScanner.Scan(masterPath, NoExclude)
            .Where(e => !e.IsDirectory)
            .ToDictionary(e => e.RelativePath, StringComparer.Ordinal);

        var entries = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        foreach (var s in staging)
        {
            if (masterByPath.TryGetValue(s.RelativePath, out var m) && IsSame(stagingPath, masterPath, s, m))
                entries[s.RelativePath] = new ManifestEntry(s.Size, s.ModifiedUtc);
        }

        return new SyncManifest { Version = 1, LastMergedAtUtc = DateTimeOffset.UtcNow, Entries = entries };
    }

    public async Task<SyncManifest?> TryGetAsync(string jobName, string deviceName, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        var rows = await Sql.QueryAsync(connection, null,
            "SELECT Json FROM Manifests WHERE JobName = @job AND DeviceName = @device;", r => r.GetString(0), ct,
            ("@job", jobName), ("@device", deviceName));
        return rows.Count == 0 ? null : JsonSerializer.Deserialize(rows[0], AppConfigJsonContext.Default.SyncManifest);
    }

    public async Task SaveAsync(string jobName, string deviceName, SyncManifest manifest, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await Sql.ExecuteAsync(connection, null, """
            INSERT INTO Manifests (JobName, DeviceName, Json) VALUES (@job, @device, @json)
            ON CONFLICT (JobName, DeviceName) DO UPDATE SET Json = excluded.Json;
            """, ct,
            ("@job", ConfigNames.Segment(jobName)), ("@device", ConfigNames.Segment(deviceName)),
            ("@json", JsonSerializer.Serialize(manifest, AppConfigJsonContext.Default.SyncManifest)));
    }

    public async Task<int> ClearAsync(string jobName, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await Sql.ExecuteAsync(connection, null, "DELETE FROM ManifestRoots WHERE JobName = @job;", ct, ("@job", jobName));
        return await Sql.ExecuteAsync(connection, null, "DELETE FROM Manifests WHERE JobName = @job;", ct, ("@job", jobName));
    }

    public async Task<ManifestRoot?> GetRootAsync(string jobName, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        var rows = await Sql.QueryAsync(connection, null, "SELECT RootId, MasterPath FROM ManifestRoots WHERE JobName = @job;",
            r => new ManifestRoot(Guid.Parse(r.GetString(0)), r.GetString(1)), ct, ("@job", jobName));
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task SetRootAsync(string jobName, ManifestRoot root, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await Sql.ExecuteAsync(connection, null, """
            INSERT INTO ManifestRoots (JobName, RootId, MasterPath) VALUES (@job, @rootId, @masterPath)
            ON CONFLICT (JobName) DO UPDATE SET RootId = excluded.RootId, MasterPath = excluded.MasterPath;
            """, ct, ("@job", ConfigNames.Segment(jobName)), ("@rootId", root.RootId.ToString()), ("@masterPath", root.MasterPath));
    }

    public async Task<bool> AnyEntriesAsync(string jobName, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        var rows = await Sql.QueryAsync(connection, null, "SELECT Json FROM Manifests WHERE JobName = @job;", r => r.GetString(0), ct, ("@job", jobName));
        return rows.Any(json => JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.SyncManifest) is { Entries.Count: > 0 });
    }

    private static bool IsSame(string stagingPath, string masterPath, FileEntry staging, FileEntry master) =>
        staging.Size == master.Size
        && (staging.ModifiedUtc - master.ModifiedUtc).Duration() <= ModifiedTolerance
        && ContentHasher.FilesAreIdentical(Path.Combine(stagingPath, staging.RelativePath), Path.Combine(masterPath, master.RelativePath));
}
