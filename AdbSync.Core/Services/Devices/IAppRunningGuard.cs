namespace AdbSync.Core.Services.Devices;

public interface IAppRunningGuard
{
    /// <summary>Checks "shell pidof &lt;appPackage&gt;" on each serial; returns the first serial with a live process, or null if none.</summary>
    Task<string?> FindRunningSerialAsync(string appPackage, IEnumerable<string> deviceSerials, CancellationToken ct = default);

    /// <summary>
    /// Polls "pidof &lt;appPackage&gt;" on <paramref name="serial"/> with short-lived adb shell calls until it stops
    /// matching. A single silent long-lived shell stream gets dropped by the wireless-debugging link every minute
    /// or two, and every reconnect re-raises the device's "wireless debugging connected" notification. Throws if
    /// the adb connection drops before the app closes; callers own reconnect/retry.
    /// </summary>
    Task WaitUntilStoppedAsync(string appPackage, string serial, CancellationToken ct = default);
}
