namespace AdbSync.Core.Orchestration;

public interface IPushSafetyGuard
{
    /// <summary>Call after each successful device pull so future push-safety checks have a historical baseline.</summary>
    Task RecordDeviceSnapshotAsync(string jobName, string deviceName, int fileCount, CancellationToken ct = default);

    /// <summary>Throws <see cref="PushSafetyException"/> if master is empty, or far below the best historical per-device file count.</summary>
    Task AssertSafeToPushAsync(string jobName, string masterPath, CancellationToken ct = default);

    /// <summary>
    /// Manual override for a blocked push: never throws, and rebases every device's historical baseline to the
    /// current master file count so the new (lower) count is treated as the accepted normal going forward.
    /// </summary>
    Task ForcePushAsync(string jobName, string masterPath, CancellationToken ct = default);
}
