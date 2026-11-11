using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Common;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Services.Transfer;

namespace AdbSync.Core.Services.Orchestration;

/// <summary>Puts a job back to a <see cref="RollbackPointStore"/> point: master is replaced with the point's master
/// and each device gets its own captured folder mirrored back. Just before overwriting, the current master is saved
/// as a new master-only point so the restore itself can be undone.</summary>
public sealed class RollbackService(
    IAdbDeviceResolver deviceResolver,
    IAdbTransferEngine transfer,
    IManifestStore manifests,
    IPushSafetyGuard pushSafety,
    ISyncLockManager lockManager,
    RollbackPointStore store)
{
    public async Task<SnapshotResult> RestoreAsync(
        SyncJobConfig job, IReadOnlyList<DeviceConfig> devices, GlobalSettings settings, string pointPath, CancellationToken ct = default)
    {
        var sourceMaster = Path.Combine(pointPath, RollbackPointStore.MasterFolderName);
        if (!Directory.Exists(sourceMaster))
            throw new InvalidOperationException($"Rollback point '{pointPath}' no longer exists.");

        var eff = job.Resolve(settings);
        var projectRoot = ProjectFolders.ProjectRoot(eff, job.Name);
        var masterPath = Path.Combine(projectRoot, "master");
        var exclude = new ExcludeMatcher(job.Exclude);

        await using var @lock = await lockManager.TryAcquireAsync(projectRoot, TimeSpan.FromHours(eff.StaleLockHours), ct)
            ?? throw new InvalidOperationException($"Job '{job.Name}' is running - wait for it to finish before restoring.");

        var capturedDevices = Path.Combine(pointPath, RollbackPointStore.DevicesFolderName);
        var masterOnly = !Directory.Exists(capturedDevices);

        var restoredDevices = 0;
        var filesCopied = 0;
        var filesDeleted = 0;
        var bytes = 0L;
        var errors = 0;
        var skipped = new List<string>();

        var sources = masterOnly
            ? job.Devices.Select(b => (b.DeviceName, Folder: masterPath)).ToList()
            : Directory.EnumerateDirectories(capturedDevices).Select(d => (DeviceName: Path.GetFileName(d), Folder: d)).ToList();

        var targets = new List<(JobDeviceBinding Binding, string Serial, string Folder)>();
        foreach (var (deviceName, folder) in sources)
        {
            var binding = job.Devices.FirstOrDefault(b => b.DeviceName == deviceName);
            if (binding is null)
            {
                skipped.Add(deviceName);
                continue;
            }

            var device = devices.FirstOrDefault(d => d.Name == binding.DeviceName)
                ?? throw new InvalidOperationException($"Device '{binding.DeviceName}' referenced by job '{job.Name}' was not found.");
            targets.Add((binding, await deviceResolver.EnsureConnectedAsync(device, ct), folder));
        }

        var undoPoint = store.Create(projectRoot);
        store.CaptureMaster(undoPoint, masterPath);

        DirectoryUtil.DeleteRecursive(masterPath);
        DirectoryUtil.CopyRecursive(sourceMaster, masterPath);

        foreach (var (binding, serial, folder) in targets)
        {
            var result = await transfer.PushMirrorAsync(serial, folder, binding.RemotePath, exclude, eff.ToTransferPolicy(), ct);
            filesCopied += result.FilesCopied;
            filesDeleted += result.FilesDeleted;
            bytes += result.BytesCopied;
            errors += result.Errors.Count;
            restoredDevices++;
        }

        manifests.Clear(job.Name);
        await pushSafety.ForcePushAsync(job.Name, masterPath, ct);

        return new SnapshotResult(pointPath, restoredDevices, filesCopied, bytes, errors, filesDeleted, skipped);
    }
}
