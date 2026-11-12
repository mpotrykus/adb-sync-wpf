using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Devices;
using AdbSync.Core.Services.Logging;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace AdbSync.Core.Services.Devices;

public sealed class AdbDeviceResolver(
    IAdbClient adbClient, IMdnsBrowser mdns, IAdbServer adbServer, string adbExecutablePath = "adb",
    ILogger<AdbDeviceResolver>? logger = null, TimeSpan? onlineWait = null) : IAdbDeviceResolver
{
    private static readonly TimeSpan DefaultOnlineWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OnlinePollInterval = TimeSpan.FromMilliseconds(500);
    private readonly TimeSpan _onlineWait = onlineWait ?? DefaultOnlineWait;

    private const string AdbTlsConnectServiceType = "_adb-tls-connect._tcp";
    private const string AdbTlsPairingServiceType = "_adb-tls-pairing._tcp";
    private static readonly TimeSpan MdnsBrowseTimeout = TimeSpan.FromSeconds(5);

    static AdbDeviceResolver()
    {
        // adb 36's built-in (openscreen) mDNS backend aborts the whole server on NSEC records, dropping every
        // wireless transport and re-raising the device's "wireless debugging connected" notification each time.
        // We browse mDNS ourselves and connect to an explicit ip:port, so adb's own discovery isn't needed.
        if (Environment.GetEnvironmentVariable("ADB_MDNS") is null)
            Environment.SetEnvironmentVariable("ADB_MDNS", "0");
    }

    private readonly ILogger<AdbDeviceResolver> _logger = new RunCapturingLogger<AdbDeviceResolver>(logger ?? NullLogger<AdbDeviceResolver>.Instance);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectGates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string> EnsureConnectedAsync(DeviceConfig device, CancellationToken ct = default)
    {
        var gate = _connectGates.GetOrAdd(device.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await EnsureConnectedCoreAsync(device, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> EnsureConnectedCoreAsync(DeviceConfig device, CancellationToken ct)
    {
        var startResult = await adbServer.StartServerAsync(adbExecutablePath, restartServerIfNewer: false, ct);
        if (startResult != StartServerResult.AlreadyRunning)
            _logger.LogDebug("Device '{Device}': adb server start result: {Result}", device.Name, startResult);

        if (device.Ip is null)
        {
            return device.Serial
                ?? throw new InvalidOperationException($"Device '{device.Name}' has neither an IP nor a serial configured.");
        }

        if (device.CachedHostPort is not null)
        {
            if (await IsOnlineAsync(device.CachedHostPort, ct))
                return device.CachedHostPort;
            if (await TryConnectCachedAsync(device, ct))
                return device.CachedHostPort;
            _logger.LogDebug(
                "Device '{Device}': cached host:port {HostPort} is no longer reachable, re-discovering", device.Name, device.CachedHostPort);
        }

        var existing = await FindOnlineHostPortAsync(device, ct);
        if (existing is not null)
        {
            _logger.LogDebug("Device '{Device}': found already-connected {HostPort} via 'adb devices'", device.Name, existing);
            Cache(device, existing);
            return existing;
        }

        if (device.StaticPort is { } staticPort)
        {
            var staticHostPort = $"{device.Ip}:{staticPort}";
            try
            {
                await adbClient.ConnectAsync(device.Ip, staticPort, ct);
                if (await WaitForStateAsync(staticHostPort, ct) == DeviceState.Online)
                {
                    Cache(device, staticHostPort);
                    return staticHostPort;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Device '{Device}': static port {HostPort} didn't connect, trying mDNS", device.Name, staticHostPort);
            }
        }

        _logger.LogDebug("Device '{Device}': browsing mDNS for {ServiceType} to find {Ip}", device.Name, AdbTlsConnectServiceType, device.Ip);
        var announcements = (await mdns.BrowseAsync(AdbTlsConnectServiceType, MdnsBrowseTimeout, ct)).ToList();
        _logger.LogDebug(
            "Device '{Device}': mDNS returned {Count} announcement(s): {Announcements}", device.Name, announcements.Count,
            string.Join("; ", announcements.Select(a => $"{a.InstanceName} @ [{string.Join(',', a.Addresses)}]:{a.Port}")));

        var candidates = announcements.Where(a => a.Addresses.Any(addr => addr.ToString() == device.Ip)).ToList();
        if (candidates.Count == 0)
        {
            throw new DeviceConnectException(device.Name, device.Ip, announcements.Count == 0
                ? "no wireless-debugging devices are being advertised on this network - enable Wireless debugging under Developer options on the device, and confirm the PC and device are on the same network/subnet"
                : $"found {announcements.Count} wireless-debugging announcement(s) but none for {device.Ip} - check the device's current IP address (it may have changed) under Settings > Developer options > Wireless debugging");
        }

        string? lastFailureReason = null;
        foreach (var candidate in candidates)
        {
            try
            {
                await adbClient.ConnectAsync(device.Ip, candidate.Port, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Device '{Device}': adb connect to {Ip}:{Port} threw", device.Name, device.Ip, candidate.Port);
                lastFailureReason = $"connecting to {device.Ip}:{candidate.Port} threw {ex.GetType().Name}: {ex.Message}";
                continue;
            }

            var hostPort = $"{device.Ip}:{candidate.Port}";
            var state = await WaitForStateAsync(hostPort, ct);
            if (state == DeviceState.Online)
            {
                Cache(device, hostPort);
                return hostPort;
            }

            _logger.LogInformation("Device '{Device}': connected to {HostPort} but adb reports state {State}", device.Name, hostPort, state);
            lastFailureReason = state is null
                ? $"connected to {hostPort} but it never showed up in 'adb devices'"
                : $"connected to {hostPort} but adb reports its state as '{state}'" + (state == DeviceState.Unauthorized
                    ? " - accept the debugging prompt on the device's screen and try again"
                    : "");
        }

        throw new DeviceConnectException(device.Name, device.Ip, lastFailureReason);
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

        return await PairCoreAsync(device, device.Ip, candidate.Port, pairingCode, ct);
    }

    public async Task<string> PairAtAsync(DeviceConfig device, int pairingPort, string pairingCode, CancellationToken ct = default)
    {
        if (device.Ip is null)
            throw new InvalidOperationException($"Device '{device.Name}' has no IP configured; pairing only applies to WiFi devices.");

        await adbServer.StartServerAsync(adbExecutablePath, restartServerIfNewer: false, ct);
        return await PairCoreAsync(device, device.Ip, pairingPort, pairingCode, ct);
    }

    private async Task<string> PairCoreAsync(DeviceConfig device, string ip, int port, string pairingCode, CancellationToken ct)
    {
        var response = await adbClient.PairAsync(ip, port, pairingCode, ct);
        if (!response.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
            throw new DevicePairException(device.Name, ip, response);

        return $"{ip}:{port}";
    }

    private async Task<bool> TryConnectCachedAsync(DeviceConfig device, CancellationToken ct)
    {
        var hostPort = device.CachedHostPort!;
        var (host, port) = SplitHostPort(hostPort);
        if (host != device.Ip)
            return false;

        try
        {
            await adbClient.ConnectAsync(host, port, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Device '{Device}': adb connect to cached {HostPort} threw", device.Name, hostPort);
            return false;
        }

        if (await WaitForStateAsync(hostPort, ct) != DeviceState.Online)
            return false;

        _logger.LogDebug("Device '{Device}': reconnected to cached {HostPort}", device.Name, hostPort);
        Cache(device, hostPort);
        return true;
    }

    private async Task<string?> FindOnlineHostPortAsync(DeviceConfig device, CancellationToken ct)
    {
        var matching = (await adbClient.GetDevicesAsync(ct))
            .Where(d => d.Serial.StartsWith($"{device.Ip}:", StringComparison.Ordinal))
            .ToList();

        if (matching.Count > 0 && matching.All(d => d.State != DeviceState.Online))
        {
            _logger.LogInformation(
                "Device '{Device}': 'adb devices' already lists {Ip} but not online: {States}",
                device.Name, device.Ip, string.Join(", ", matching.Select(d => $"{d.Serial}={d.State}")));
        }

        return matching.FirstOrDefault(d => d.State == DeviceState.Online)?.Serial;
    }

    private async Task<DeviceState?> GetStateAsync(string hostPort, CancellationToken ct)
    {
        var devices = await adbClient.GetDevicesAsync(ct);
        return devices.FirstOrDefault(d => d.Serial == hostPort)?.State;
    }

    /// <summary>A just-made wireless connection can take a few seconds to show up in 'adb devices' (or show as offline
    /// first), so a single immediate look reports a healthy device as missing. Polls until it is online or the wait is up.</summary>
    private async Task<DeviceState?> WaitForStateAsync(string hostPort, CancellationToken ct)
    {
        var state = await GetStateAsync(hostPort, ct);
        var deadline = DateTime.UtcNow + _onlineWait;
        while (state != DeviceState.Online && state != DeviceState.Unauthorized && DateTime.UtcNow < deadline)
        {
            await Task.Delay(OnlinePollInterval, ct);
            state = await GetStateAsync(hostPort, ct);
        }
        return state;
    }

    private async Task<bool> IsOnlineAsync(string hostPort, CancellationToken ct) =>
        await GetStateAsync(hostPort, ct) == DeviceState.Online;

    public async Task DisconnectAsync(DeviceConfig device, CancellationToken ct = default)
    {
        if (device.CachedHostPort is null)
            return;

        var (host, port) = SplitHostPort(device.CachedHostPort);
        try
        {
            await adbClient.DisconnectAsync(host, port, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Device '{Device}': adb disconnect from {HostPort} threw", device.Name, device.CachedHostPort);
        }
        finally
        {
            device.CachedHostPort = null;
            device.CachedAt = null;
        }
    }

    private static (string Host, int Port) SplitHostPort(string hostPort)
    {
        var separatorIndex = hostPort.LastIndexOf(':');
        return (hostPort[..separatorIndex], int.Parse(hostPort[(separatorIndex + 1)..]));
    }

    private static void Cache(DeviceConfig device, string hostPort)
    {
        device.CachedHostPort = hostPort;
        device.CachedAt = DateTimeOffset.UtcNow;
    }
}
