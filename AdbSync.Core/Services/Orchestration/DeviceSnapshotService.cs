using System.Globalization;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Devices;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Transfer;

namespace AdbSync.Core.Services.Orchestration;

public sealed class DeviceSnapshotService(IAdbDeviceResolver deviceResolver, IAdbTransferEngine transfer) : IDeviceSnapshotService
{
    private const string TimestampFormat = "yyyy-MM-dd_HH-mm-ss";

    public async Task<SnapshotResult> CreateSnapshotAsync(
        SyncJobConfig job, IReadOnlyList<DeviceConfig> devices, GlobalSettings settings, CancellationToken ct = default)
    {
        var eff = job.Resolve(settings);
        var checkpointsRoot = GetCheckpointsRoot(job, eff);
        // Sibling of "master"/".sync_staging"/".sync_conflicts" - pull/push/merge only ever touch those by name,
        // so this folder is never read from or written to by a sync run.
        var snapshotRoot = Path.Combine(checkpointsRoot, DateTimeOffset.Now.ToString(TimestampFormat));
        var exclude = new ExcludeMatcher(job.Exclude);

        var totalFiles = 0;
        var totalBytes = 0L;
        var errors = 0;

        foreach (var binding in job.Devices)
        {
            var device = devices.FirstOrDefault(d => d.Name == binding.DeviceName)
                ?? throw new InvalidOperationException($"Device '{binding.DeviceName}' referenced by job '{job.Name}' was not found.");
            var serial = await deviceResolver.EnsureConnectedAsync(device, ct);

            var destination = Path.Combine(snapshotRoot, binding.DeviceName);
            Directory.CreateDirectory(destination);
            var result = await transfer.PullMirrorAsync(serial, binding.RemotePath, destination, exclude, eff.ToTransferPolicy(), ct);
            totalFiles += result.FilesCopied;
            totalBytes += result.BytesCopied;
            errors += result.Errors.Count;
        }

        PruneOldSnapshots(checkpointsRoot, eff.CheckpointRetentionDays);

        return new SnapshotResult(snapshotRoot, job.Devices.Count, totalFiles, totalBytes, errors);
    }

    public IReadOnlyList<SnapshotInfo> ListSnapshots(SyncJobConfig job, GlobalSettings settings)
    {
        var checkpointsRoot = GetCheckpointsRoot(job, job.Resolve(settings));
        if (!Directory.Exists(checkpointsRoot))
            return [];

        var snapshots = new List<SnapshotInfo>();
        foreach (var dir in Directory.EnumerateDirectories(checkpointsRoot))
        {
            var name = Path.GetFileName(dir);
            if (!DateTimeOffset.TryParseExact(name, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var createdAt))
                continue; // not a checkpoint folder we created - ignore rather than fail the whole list

            var deviceNames = Directory.EnumerateDirectories(dir)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()!;
            snapshots.Add(new SnapshotInfo(dir, createdAt, deviceNames!));
        }

        return snapshots.OrderByDescending(s => s.CreatedAt).ToList();
    }

    public async Task<SnapshotResult> RestoreSnapshotAsync(
        SyncJobConfig job, IReadOnlyList<DeviceConfig> devices, GlobalSettings settings, string snapshotPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(snapshotPath))
            throw new InvalidOperationException($"Checkpoint '{snapshotPath}' no longer exists.");

        var eff = job.Resolve(settings);
        var exclude = new ExcludeMatcher(job.Exclude);
        var restoredDevices = 0;
        var totalFilesCopied = 0;
        var totalFilesDeleted = 0;
        var totalBytes = 0L;
        var errors = 0;
        var skipped = new List<string>();

        foreach (var deviceDir in Directory.EnumerateDirectories(snapshotPath))
        {
            var deviceName = Path.GetFileName(deviceDir);
            var binding = job.Devices.FirstOrDefault(b => b.DeviceName == deviceName);
            if (binding is null)
            {
                skipped.Add(deviceName);
                continue;
            }

            var device = devices.FirstOrDefault(d => d.Name == binding.DeviceName)
                ?? throw new InvalidOperationException($"Device '{binding.DeviceName}' referenced by job '{job.Name}' was not found.");
            var serial = await deviceResolver.EnsureConnectedAsync(device, ct);

            var result = await transfer.PushMirrorAsync(serial, deviceDir, binding.RemotePath, exclude, eff.ToTransferPolicy(), ct);
            totalFilesCopied += result.FilesCopied;
            totalFilesDeleted += result.FilesDeleted;
            totalBytes += result.BytesCopied;
            errors += result.Errors.Count;
            restoredDevices++;
        }

        return new SnapshotResult(snapshotPath, restoredDevices, totalFilesCopied, totalBytes, errors, totalFilesDeleted, skipped);
    }

    private static string GetCheckpointsRoot(SyncJobConfig job, EffectiveJobSettings eff) =>
        Path.Combine(eff.ProjectsDirectory, job.Name, "checkpoints");

    // Opportunistic sweep, mirroring SyncJobRunner.PruneConflictBackups: piggyback on the run that just
    // created a new checkpoint rather than running a separate cleanup job.
    private static void PruneOldSnapshots(string checkpointsRoot, int retentionDays)
    {
        if (retentionDays <= 0 || !Directory.Exists(checkpointsRoot))
            return;

        var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-retentionDays).UtcDateTime;
        foreach (var dir in Directory.EnumerateDirectories(checkpointsRoot))
        {
            var name = Path.GetFileName(dir);
            if (DateTimeOffset.TryParseExact(name, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var createdAt)
                && createdAt.UtcDateTime < cutoffUtc)
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
