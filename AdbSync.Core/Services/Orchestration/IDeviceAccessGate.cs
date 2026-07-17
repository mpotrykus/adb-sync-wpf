namespace AdbSync.Core.Services.Orchestration;

/// <summary>
/// Process-wide per-device-name semaphore. Guards against more than <paramref name="maxConcurrent"/> things
/// touching the same physical device's adb connection at once (a sync run and a snapshot/restore, or two sync
/// runs sharing a device across jobs). Acquired independently per device name so a caller that needs several
/// devices isn't blocked on a single busy one - fire off one acquisition per device (e.g. via Task.WhenAll) and
/// each becomes available on its own schedule instead of forcing the whole caller to wait in a fixed order.
/// </summary>
public interface IDeviceAccessGate
{
    /// <summary>Acquires one of <paramref name="maxConcurrent"/> slots for <paramref name="deviceName"/>. The
    /// per-device slot count is fixed by whichever caller acquires that device name first (from
    /// <see cref="AdbSync.Core.Models.Config.GlobalSettings.MaxConcurrentPerDevice"/>) and does not change for later callers
    /// passing a different value until the process restarts.</summary>
    Task<IAsyncDisposable> AcquireAsync(string deviceName, int maxConcurrent, CancellationToken ct = default);

    /// <summary>True if the given device is currently held by someone else - a point-in-time hint for status
    /// reporting (e.g. "show a waiting message before blocking on AcquireAsync"), not a reservation, so it can
    /// go stale the instant it's read. Never gate actual device access on this - only AcquireAsync does that.</summary>
    bool IsBusy(string deviceName);
}
