namespace AdbSync.Core.Services.Devices;

public interface IDevicePackageLister
{
    /// <summary>Lists third-party (user-installed) app packages via "pm list packages -3".</summary>
    Task<IReadOnlyList<string>> ListInstalledPackagesAsync(string serial, CancellationToken ct = default);
}
