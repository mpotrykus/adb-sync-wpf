namespace AdbSync.Core.Models.Orchestration;

/// <summary><paramref name="SkippedDevices"/> is only ever non-empty for a restore: checkpoint subfolders whose
/// device name isn't (or is no longer) bound to the job, so there's no known RemotePath to push to.</summary>
public sealed record SnapshotResult(
    string SnapshotPath, int DeviceCount, int TotalFiles, long TotalBytes, int Errors,
    int TotalFilesDeleted = 0, IReadOnlyList<string>? SkippedDevices = null);
