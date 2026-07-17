namespace AdbSync.Core.Services.Devices;

public interface IAppRunningGuard
{
    /// <summary>Checks "shell pidof &lt;appPackage&gt;" on each serial; returns the first serial with a live process, or null if none.</summary>
    Task<string?> FindRunningSerialAsync(string appPackage, IEnumerable<string> deviceSerials, CancellationToken ct = default);

    /// <summary>
    /// Blocks on a single long-lived adb shell call until "pidof &lt;appPackage&gt;" stops matching on
    /// <paramref name="serial"/> - the device itself polls in a loop, so the host makes one round trip and just
    /// awaits it instead of re-checking on an interval. Throws if the adb connection drops before the app closes;
    /// callers own reconnect/retry.
    /// </summary>
    Task WaitUntilStoppedAsync(string appPackage, string serial, CancellationToken ct = default);
}
