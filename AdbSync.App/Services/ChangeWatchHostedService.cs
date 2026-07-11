using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Models.Devices;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdbSync.App.Services;

/// <summary>
/// Keeps one live <see cref="ChangeWatchCoordinator"/> running per enabled OnChange job, reconciled against
/// <see cref="AppConfigService"/> - which already raises <see cref="AppConfigService.ConfigChanged"/> on every
/// save from the job editor - so watchers start/stop/restart immediately when a job is added, edited, disabled,
/// or removed, without a separate polling loop.
/// </summary>
public sealed class ChangeWatchHostedService(
    AppConfigService configService,
    IDeviceChangeWatcher watcher,
    IAdbDeviceResolver deviceResolver,
    ISyncEventSink events,
    JobRunService jobRunService,
    ILogger<ChangeWatchHostedService> logger) : BackgroundService
{
    private readonly Dictionary<string, ActiveWatch> _active = [];
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        configService.ConfigChanged += OnConfigChanged;
        await ReconcileAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        configService.ConfigChanged -= OnConfigChanged;
        await base.StopAsync(cancellationToken);

        foreach (var (jobName, active) in _active)
        {
            try
            {
                await active.Coordinator.DisposeAsync().AsTask().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown deadline hit while a watch loop was still unwinding - abandon it; the
                // process is exiting regardless.
                logger.LogWarning("Watch for job '{Job}' didn't stop within the shutdown deadline; abandoning it", jobName);
            }
        }
        _active.Clear();
    }

    private async void OnConfigChanged(object? sender, EventArgs e)
    {
        try
        {
            await ReconcileAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // best-effort reconciliation - a bad tick shouldn't take down the app; the next config save retries
            logger.LogWarning(ex, "Failed to reconcile change watchers after a config change");
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        await _reconcileGate.WaitAsync(ct);
        try
        {
            var config = await configService.GetAsync();
            var wanted = config.Jobs
                .Where(j => j.Enabled && j.Schedule.Kind == ScheduleKind.OnChange && j.Devices.Count > 0)
                .ToDictionary(j => j.Name);

            foreach (var name in _active.Keys.ToList())
            {
                if (!wanted.TryGetValue(name, out var job) || _active[name].Signature != Signature(job))
                {
                    await _active[name].Coordinator.DisposeAsync();
                    _active.Remove(name);
                }
            }

            foreach (var job in wanted.Values)
            {
                if (_active.ContainsKey(job.Name))
                    continue;

                var bindings = job.Devices
                    .Select(b => new ChangeWatchBinding(config.Devices.First(d => d.Name == b.DeviceName), b.RemotePath))
                    .ToList();

                var jobName = job.Name;
                var coordinator = new ChangeWatchCoordinator(
                    jobName, bindings, watcher, deviceResolver, events,
                    job.Schedule.DebounceWindow, job.Schedule.RescanInterval,
                    onTriggered: () => TriggerAsync(jobName),
                    logger: logger);
                coordinator.Start();
                _active[job.Name] = new ActiveWatch(coordinator, Signature(job));
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task TriggerAsync(string jobName)
    {
        var config = await configService.GetAsync();
        var index = config.Jobs.FindIndex(j => j.Name == jobName);

        if (index >= 0 && config.Jobs[index].Enabled)
            await jobRunService.RunJobAsync(index);
    }

    private static string Signature(SyncJobConfig job) => string.Join('|',
        job.Schedule.DebounceWindow, job.Schedule.RescanInterval,
        string.Join(',', job.Devices.Select(b => $"{b.DeviceName}={b.RemotePath}")));

    private sealed record ActiveWatch(ChangeWatchCoordinator Coordinator, string Signature);
}
