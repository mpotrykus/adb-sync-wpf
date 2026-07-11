namespace AdbSync.Core.Models.Orchestration;

/// <summary>One stored checkpoint folder for a job - <see cref="DeviceNames"/> are read straight off its
/// subfolder names, which may include devices no longer bound to the job.</summary>
public sealed record SnapshotInfo(string Path, DateTimeOffset CreatedAt, IReadOnlyList<string> DeviceNames);
