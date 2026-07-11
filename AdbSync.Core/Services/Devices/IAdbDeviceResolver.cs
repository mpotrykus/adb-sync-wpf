using AdbSync.Core.Models.Devices;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;

namespace AdbSync.Core.Services.Devices;

public interface IAdbDeviceResolver
{
    /// <summary>
    /// Ensures <paramref name="device"/> is connected and returns an adb-ready identifier ("ip:port" for WiFi,
    /// the raw serial for USB). On success for a WiFi device, mutates <see cref="DeviceConfig.CachedHostPort"/>/
    /// <see cref="DeviceConfig.CachedAt"/> in place - callers are responsible for persisting the config afterward.
    /// </summary>
    Task<string> EnsureConnectedAsync(DeviceConfig device, CancellationToken ct = default);

    /// <summary>
    /// Performs the one-time Android 11+ wireless-debugging pairing handshake against <paramref name="device"/>'s
    /// configured IP, using <paramref name="pairingCode"/> (the 6-digit code shown on the device's "Pair device
    /// with pairing code" screen). Discovers the (ephemeral, per-attempt) pairing port via mDNS - the device must
    /// have that screen open for its <c>_adb-tls-pairing._tcp</c> announcement to be visible. Returns the paired
    /// "ip:port" on success; this is unrelated to (and does not populate) <see cref="DeviceConfig.CachedHostPort"/>,
    /// which is only ever set by a subsequent <see cref="EnsureConnectedAsync"/> connect.
    /// </summary>
    Task<string> PairAsync(DeviceConfig device, string pairingCode, CancellationToken ct = default);
}
