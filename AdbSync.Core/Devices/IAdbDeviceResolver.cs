using AdbSync.Core.Config;

namespace AdbSync.Core.Devices;

public interface IAdbDeviceResolver
{
    /// <summary>
    /// Ensures <paramref name="device"/> is connected and returns an adb-ready identifier ("ip:port" for WiFi,
    /// the raw serial for USB). On success for a WiFi device, mutates <see cref="DeviceConfig.CachedHostPort"/>/
    /// <see cref="DeviceConfig.CachedAt"/> in place - callers are responsible for persisting the config afterward.
    /// </summary>
    Task<string> EnsureConnectedAsync(DeviceConfig device, CancellationToken ct = default);
}
