namespace AdbSync.Core.Models.Orchestration;

public enum SnapshotKind
{
    /// <summary>A user-triggered pull of the devices' current state.</summary>
    Manual,
    /// <summary>Taken automatically before a sync merged anything: master plus each device's pulled folder.</summary>
    RollbackPoint,
}

/// <summary>One stored checkpoint folder for a job - <see cref="DeviceNames"/> are read straight off its
/// subfolder names, which may include devices no longer bound to the job.</summary>
public sealed record SnapshotInfo(
    string Path, DateTimeOffset CreatedAt, IReadOnlyList<string> DeviceNames, SnapshotKind Kind = SnapshotKind.Manual);
