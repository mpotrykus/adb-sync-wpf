namespace AdbSync.Core.Services.Devices;

public interface IDeviceAppControl
{
    /// <summary>Kills the app's process on the device (<c>am force-stop</c>), discarding anything it hadn't saved.</summary>
    Task ForceStopAsync(string appPackage, string serial, CancellationToken ct = default);

    /// <summary>Starts the app's launcher activity on the device. Returns false if the package has no launcher activity.</summary>
    Task<bool> LaunchAsync(string appPackage, string serial, CancellationToken ct = default);
}
