using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Companion;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace AdbSync.Engine.Services;

/// <summary>Delivers <see cref="CompanionEventSink"/> events to the companion app on each of the job's devices
/// that adb already has online, and periodically tells those devices how to reach the companion server. Never
/// connects a device itself, so it can't keep Wi-Fi sockets alive across a suspend; each send is a short-lived
/// shell command.</summary>
public sealed class CompanionBroadcastService : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstProvisionDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private readonly IAdbClient _adbClient;
    private readonly AppConfigService _configService;
    private readonly CompanionCertificateStore _certificates;
    private readonly ILogger<CompanionBroadcastService> _logger;
    private readonly Channel<CompanionEvent> _queue =
        Channel.CreateUnbounded<CompanionEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<string> _granted = new(StringComparer.OrdinalIgnoreCase);

    public CompanionBroadcastService(
        IAdbClient adbClient, AppConfigService configService, CompanionCertificateStore certificates,
        ILogger<CompanionBroadcastService> logger)
    {
        _adbClient = adbClient;
        _configService = configService;
        _certificates = certificates;
        _logger = logger;
        Sink = new CompanionEventSink(e => _queue.Writer.TryWrite(e));
    }

    public CompanionEventSink Sink { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var periodic = PeriodicLoopAsync(stoppingToken);
        try
        {
            await foreach (var e in _queue.Reader.ReadAllAsync(stoppingToken))
                await SendAsync(e, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        await periodic;
    }

    private async Task PeriodicLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstProvisionDelay, ct);
            await ProvisionAsync(ct);

            using var timer = new PeriodicTimer(HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var e in Sink.ActiveHeartbeats())
                    _queue.Writer.TryWrite(e);
                await ProvisionAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task SendAsync(CompanionEvent e, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(SendTimeout);
        try
        {
            var config = await _configService.GetAsync();
            if (!config.Settings.CompanionBroadcastsEnabled)
                return;

            var job = config.Jobs.FirstOrDefault(j => j.Name == e.JobName);
            if (job is null)
                return;

            var devices = job.Devices
                .Select(b => config.Devices.FirstOrDefault(d => d.Name == b.DeviceName))
                .OfType<DeviceConfig>();
            var command = CompanionBroadcast.BuildCommand(e);
            foreach (var device in await GetOnlineAsync(devices, cts.Token))
                await ShellAsync(device, command, $"{e.Kind} for '{e.JobName}'", cts.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Companion {Kind} for '{Job}' not delivered", e.Kind, e.JobName);
        }
    }

    private async Task ProvisionAsync(CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(SendTimeout);
        try
        {
            var config = await _configService.GetAsync();
            var settings = config.Settings;
            if (!settings.CompanionServerEnabled || string.IsNullOrEmpty(settings.CompanionServerToken))
                return;

            var online = (await GetOnlineAsync(config.Devices, cts.Token)).ToList();
            foreach (var deviceConfig in config.Devices)
            {
                var device = online.FirstOrDefault(d =>
                    string.Equals(d.Serial, deviceConfig.CachedHostPort, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.Serial, deviceConfig.Serial, StringComparison.OrdinalIgnoreCase));
                if (device is null)
                    continue;

                var localIp = LocalAddressFor(device.Serial);
                if (localIp is null)
                    continue;

                if (_granted.Add(device.Serial))
                    await ShellAsync(device, CompanionBroadcast.GrantSecureSettingsCommand, "pm grant", cts.Token);

                var url = $"https://{localIp}:{settings.CompanionServerPort}";
                var command = CompanionBroadcast.BuildConfigCommand(
                    url, settings.CompanionServerToken, _certificates.Sha256Fingerprint, Environment.MachineName, deviceConfig.Name);
                await ShellAsync(device, command, "config", cts.Token);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Companion provisioning skipped");
        }
    }

    private async Task<IEnumerable<DeviceData>> GetOnlineAsync(IEnumerable<DeviceConfig> devices, CancellationToken ct)
    {
        var candidates = devices
            .SelectMany(d => new[] { d.CachedHostPort, d.Serial })
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (candidates.Count == 0)
            return [];

        var online = await _adbClient.GetDevicesAsync(ct);
        return online.Where(d => d.State == DeviceState.Online && candidates.Contains(d.Serial)).ToList();
    }

    private async Task ShellAsync(DeviceData device, string command, string what, CancellationToken ct)
    {
        var receiver = new ConsoleOutputReceiver();
        await _adbClient.ExecuteRemoteCommandAsync(command, device, receiver, Encoding.UTF8, ct);
        _logger.LogDebug("Companion {What} -> {Serial}: {Output}", what, device.Serial, receiver.ToString().Trim());
    }

    /// <summary>The PC's address on the interface that routes to the device ("ip:port" serial), or the first
    /// routable IPv4 address for a USB device.</summary>
    private static IPAddress? LocalAddressFor(string serial)
    {
        var host = serial.Split(':')[0];
        if (IPAddress.TryParse(host, out var deviceIp))
        {
            try
            {
                using var probe = new Socket(deviceIp.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                probe.Connect(deviceIp, 9);
                return (probe.LocalEndPoint as IPEndPoint)?.Address;
            }
            catch (SocketException)
            {
                return null;
            }
        }

        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetIPProperties())
            .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(p => p.UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
    }
}
