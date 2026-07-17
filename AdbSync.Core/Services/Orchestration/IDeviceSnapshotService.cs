using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Orchestration;

namespace AdbSync.Core.Services.Orchestration;

/// <summary>
/// Pulls the current on-device state of a job's devices into a standalone, timestamped folder that pull/push/
/// merge never read from or write to - a user-triggered backup point, distinct from the resumable-run
/// <see cref="SyncCheckpoint"/>/<see cref="ICheckpointManager"/> machinery despite the similar name.
/// </summary>
public interface IDeviceSnapshotService
{
    Task<SnapshotResult> CreateSnapshotAsync(
        SyncJobConfig job, IReadOnlyList<DeviceConfig> devices, GlobalSettings settings, CancellationToken ct = default);

    /// <summary>Lists this job's stored checkpoints, most recent first.</summary>
    IReadOnlyList<SnapshotInfo> ListSnapshots(SyncJobConfig job, GlobalSettings settings);

    /// <summary>Pushes a stored checkpoint's per-device folders back out to whichever of them are still bound to
    /// the job, mirroring device contents to match (deletes device files absent from the checkpoint). Checkpoint
    /// device folders with no matching binding are skipped and reported via <see cref="SnapshotResult.SkippedDevices"/>.</summary>
    Task<SnapshotResult> RestoreSnapshotAsync(
        SyncJobConfig job, IReadOnlyList<DeviceConfig> devices, GlobalSettings settings, string snapshotPath, CancellationToken ct = default);
}
