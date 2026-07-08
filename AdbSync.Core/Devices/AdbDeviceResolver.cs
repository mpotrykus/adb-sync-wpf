using AdbSync.Core.Config;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;

namespace AdbSync.Core.Devices;

public sealed class AdbDeviceResolver(IAdbClient adbClient, IMdnsBrowser mdns, IAdbServer adbServer, string adbExecutablePath = "adb") : IAdbDeviceResolver
{
    private const string AdbTlsConnectServiceType = "_adb-tls-connect._tcp";
    private const string AdbTlsPairingServiceType = "_adb-tls-pairing._tcp";
    private static readonly TimeSpan MdnsBrowseTimeout = TimeSpan.FromSeconds(5);

    public async Task<string> EnsureConnectedAsync(DeviceConfig device, CancellationToken ct = default)
    {
        // AdvancedSharpAdbClient's IAdbClient talks to the adb server daemon over a raw socket on 127.0.0.1:5037
        // and never starts it - unlike shelling out to adb.exe directly, nothing here auto-launches the daemon,
        // so every caller relied on some other process (a terminal, Android Studio, ...) having started it first.
        await adbServer.StartServerAsync(adbExecutablePath, restartServerIfNewer: false, ct);

        if (device.Ip is null)
        {
            return device.Serial
                ?? throw new InvalidOperationException($"Device '{device.Name}' has neither an IP nor a serial configured.");
        }

        if (device.CachedHostPort is not null && await IsOnlineAsync(device.CachedHostPort, ct))
            return device.CachedHostPort;

        var existing = await FindOnlineHostPortAsync(device.Ip, ct);
        if (existing is not null)
        {
            Cache(device, existing);
            return existing;
        }

        var announcements = await mdns.BrowseAsync(AdbTlsConnectServiceType, MdnsBrowseTimeout, ct);
        foreach (var candidate in announcements.Where(a => a.Addresses.Any(addr => addr.ToString() == device.Ip)))
        {
            await adbClient.ConnectAsync(device.Ip, candidate.Port, ct);
            var hostPort = $"{device.Ip}:{candidate.Port}";
            if (await IsOnlineAsync(hostPort, ct))
            {
                Cache(device, hostPort);
                return hostPort;
            }
        }

        throw new DeviceConnectException(device.Name, device.Ip);
    }

    public async Task<string> PairAsync(DeviceConfig device, string pairingCode, CancellationToken ct = default)
    {
        if (device.Ip is null)
            throw new InvalidOperationException($"Device '{device.Name}' has no IP configured; pairing only applies to WiFi devices.");

        await adbServer.StartServerAsync(adbExecutablePath, restartServerIfNewer: false, ct);

        var announcements = await mdns.BrowseAsync(AdbTlsPairingServiceType, MdnsBrowseTimeout, ct);
        var candidate = announcements.FirstOrDefault(a => a.Addresses.Any(addr => addr.ToString() == device.Ip));
        if (candidate is null)
        {
            throw new DevicePairException(device.Name, device.Ip,
                "No pairing announcement found. Open 'Pair device with pairing code' under Wireless debugging on the device and try again.");
        }

        var response = await adbClient.PairAsync(device.Ip, candidate.Port, pairingCode, ct);
        if (!response.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
            throw new DevicePairException(device.Name, device.Ip, response);

        return $"{device.Ip}:{candidate.Port}";
    }

    private async Task<string?> FindOnlineHostPortAsync(string ip, CancellationToken ct)
    {
        var devices = await adbClient.GetDevicesAsync(ct);
        return devices
            .FirstOrDefault(d => d.State == DeviceState.Online && d.Serial.StartsWith($"{ip}:", StringComparison.Ordinal))
            ?.Serial;
    }

    private async Task<bool> IsOnlineAsync(string hostPort, CancellationToken ct)
    {
        var devices = await adbClient.GetDevicesAsync(ct);
        return devices.Any(d => d.Serial == hostPort && d.State == DeviceState.Online);
    }

    private static void Cache(DeviceConfig device, string hostPort)
    {
        device.CachedHostPort = hostPort;
        device.CachedAt = DateTimeOffset.UtcNow;
    }
}
