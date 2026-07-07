namespace AdbSync.Core.Devices;

public interface IAppRunningGuard
{
    /// <summary>Checks "shell pidof &lt;appPackage&gt;" on each serial; true if the app has a live process anywhere.</summary>
    Task<bool> IsRunningAnywhereAsync(string appPackage, IEnumerable<string> deviceSerials, CancellationToken ct = default);
}
