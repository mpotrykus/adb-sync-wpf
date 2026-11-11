using AdbSync.Core.Models.Merge;
using AdbSync.Core.Services.Common;
using AdbSync.Core.Services.Config;
using System.Security.Cryptography;
using System.Text;

namespace AdbSync.Core.Services.Merge;

public sealed class PendingConflictStore(AppPaths paths) : IPendingConflictStore
{
    private static readonly TimeSpan ModifiedTolerance = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string MakeId(string jobName, string deviceName, string relativePath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{jobName}\n{deviceName}\n{relativePath}")))[..16].ToLowerInvariant();

    public async Task<IReadOnlyList<PendingConflict>> ListAsync(string? jobName = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var result = new List<PendingConflict>();
            if (!Directory.Exists(paths.PendingConflictsDir))
                return result;

            foreach (var jobDir in Directory.GetDirectories(paths.PendingConflictsDir))
            {
                if (jobName is not null && !string.Equals(Path.GetFileName(jobDir), jobName, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var file in Directory.GetFiles(jobDir, "*.pending.json"))
                {
                    var loaded = await JsonFileIo.ReadAsync(file, AppConfigJsonContext.Default.PendingConflictsFile, ct);
                    if (loaded is not null)
                        result.AddRange(loaded.Items);
                }
            }
            return result.OrderBy(c => c.JobName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.RelativePath, StringComparer.Ordinal).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PendingConflict?> GetAsync(string id, CancellationToken ct = default) =>
        (await ListAsync(null, ct)).FirstOrDefault(c => c.Id == id);

    public async Task<int> ReplaceDeviceAsync(
        string jobName, string deviceName, IReadOnlyList<HeldConflict> held, string stagingPath, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var filePath = GetFilePath(jobName, deviceName);
            var existing = (await JsonFileIo.ReadAsync(filePath, AppConfigJsonContext.Default.PendingConflictsFile, ct))?.Items ?? [];
            var existingById = existing.ToDictionary(c => c.Id, StringComparer.Ordinal);
            var next = new List<PendingConflict>();
            var added = 0;

            foreach (var h in held)
            {
                var id = MakeId(jobName, deviceName, h.RelativePath);
                existingById.TryGetValue(id, out var previous);
                if (previous is null)
                    added++;

                var conflict = new PendingConflict
                {
                    Id = id,
                    JobName = jobName,
                    DeviceName = deviceName,
                    RelativePath = h.RelativePath,
                    DetectedAtUtc = previous?.DetectedAtUtc ?? DateTimeOffset.UtcNow,
                    DeviceSize = h.StagingSize,
                    DeviceModifiedUtc = h.StagingModifiedUtc,
                    MasterSize = h.MasterSize,
                    MasterModifiedUtc = h.MasterModifiedUtc,
                };

                var copyPath = GetCopyPath(conflict);
                if (h.StagingSize is null)
                {
                    DeleteQuietly(copyPath);
                }
                else if (!File.Exists(copyPath) || previous is null || previous.DeviceSize != h.StagingSize || !SameTime(previous.DeviceModifiedUtc, h.StagingModifiedUtc))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);
                    File.Copy(Path.Combine(stagingPath, h.RelativePath), copyPath, overwrite: true);
                }

                next.Add(conflict);
            }

            var keep = next.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in existing.Where(c => !keep.Contains(c.Id)))
                DeleteQuietly(GetCopyPath(gone));

            if (next.Count == 0)
                DeleteQuietly(filePath);
            else
                await JsonFileIo.WriteAtomicAsync(filePath, new PendingConflictsFile { Items = next }, AppConfigJsonContext.Default.PendingConflictsFile, ct);
            return added;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? GetDeviceCopyPath(PendingConflict conflict)
    {
        var path = GetCopyPath(conflict);
        return File.Exists(path) ? path : null;
    }

    public async Task RemoveAsync(PendingConflict conflict, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var filePath = GetFilePath(conflict.JobName, conflict.DeviceName);
            var items = (await JsonFileIo.ReadAsync(filePath, AppConfigJsonContext.Default.PendingConflictsFile, ct))?.Items ?? [];
            items.RemoveAll(c => c.Id == conflict.Id);
            DeleteQuietly(GetCopyPath(conflict));
            if (items.Count == 0)
                DeleteQuietly(filePath);
            else
                await JsonFileIo.WriteAtomicAsync(filePath, new PendingConflictsFile { Items = items }, AppConfigJsonContext.Default.PendingConflictsFile, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetFilePath(string jobName, string deviceName) =>
        Path.Combine(paths.PendingConflictsDir, ConfigNames.Segment(jobName), $"{ConfigNames.Segment(deviceName)}.pending.json");

    private string GetCopyPath(PendingConflict conflict) =>
        Path.Combine(paths.PendingConflictsDir, ConfigNames.Segment(conflict.JobName), ConfigNames.Segment(conflict.DeviceName), $"{conflict.Id}.blob");

    private static bool SameTime(DateTimeOffset? a, DateTimeOffset? b) =>
        a is not null && b is not null && (a.Value - b.Value).Duration() <= ModifiedTolerance;

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
