using AdbSync.Core.Config;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;

namespace AdbSync.Core.Devices;

public sealed class AdbDeviceResolver(IAdbClient adbClient, IMdnsBrowser mdns) : IAdbDeviceResolver
{
    private const string AdbTlsConnectServiceType = "_adb-tls-connect._tcp";
    private static readonly TimeSpan MdnsBrowseTimeout = TimeSpan.FromSeconds(5);

    public async Task<string> EnsureConnectedAsync(DeviceConfig device, CancellationToken ct = default)
    {
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
