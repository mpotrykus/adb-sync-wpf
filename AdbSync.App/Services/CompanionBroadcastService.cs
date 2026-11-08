using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Companion;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Threading.Channels;

namespace AdbSync.App.Services;

/// <summary>Delivers <see cref="CompanionEventSink"/> events to the companion app on each of the job's devices
/// that adb already has online. Never connects a device itself, so it can't keep Wi-Fi sockets alive across a
/// suspend; each send is a short-lived shell command.</summary>
public sealed class CompanionBroadcastService : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private readonly IAdbClient _adbClient;
    private readonly AppConfigService _configService;
    private readonly ILogger<CompanionBroadcastService> _logger;
    private readonly Channel<CompanionEvent> _queue =
        Channel.CreateUnbounded<CompanionEvent>(new UnboundedChannelOptions { SingleReader = true });

    public CompanionBroadcastService(IAdbClient adbClient, AppConfigService configService, ILogger<CompanionBroadcastService> logger)
    {
        _adbClient = adbClient;
        _configService = configService;
        _logger = logger;
        Sink = new CompanionEventSink(e => _queue.Writer.TryWrite(e));
    }

    public CompanionEventSink Sink { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var heartbeat = HeartbeatLoopAsync(stoppingToken);
        try
        {
            await foreach (var e in _queue.Reader.ReadAllAsync(stoppingToken))
                await SendAsync(e, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        await heartbeat;
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var e in Sink.ActiveHeartbeats())
                    _queue.Writer.TryWrite(e);
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

            var candidates = job.Devices
                .Select(b => config.Devices.FirstOrDefault(d => d.Name == b.DeviceName))
                .OfType<DeviceConfig>()
                .SelectMany(d => new[] { d.CachedHostPort, d.Serial })
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (candidates.Count == 0)
                return;

            var online = await _adbClient.GetDevicesAsync(cts.Token);
            var command = CompanionBroadcast.BuildCommand(e);
            foreach (var device in online.Where(d => d.State == DeviceState.Online && candidates.Contains(d.Serial)))
            {
                var receiver = new ConsoleOutputReceiver();
                await _adbClient.ExecuteRemoteCommandAsync(command, device, receiver, Encoding.UTF8, cts.Token);
                _logger.LogDebug("Companion {Kind} for '{Job}' -> {Serial}: {Output}", e.Kind, e.JobName, device.Serial, receiver.ToString().Trim());
            }
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
}
