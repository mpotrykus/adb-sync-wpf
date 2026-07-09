using AdbSync.Core.Config;
using AdbSync.Core.Devices;
using AdbSync.Core.Orchestration;
using Microsoft.Extensions.Hosting;

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
    JobRunService jobRunService) : BackgroundService
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

        // Each coordinator's binding loops only unwind once their in-flight adb read (e.g. a live
        // "inotifyd" watch, which blocks until the device reports a change) observes cancellation -
        // that can hang indefinitely if the device is slow/unresponsive. Bounding this by the host's
        // own shutdown token guarantees app exit never hangs waiting for it.
        foreach (var active in _active.Values)
        {
            try
            {
                await active.Coordinator.DisposeAsync().AsTask().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown deadline hit while a watch loop was still unwinding - abandon it; the
                // process is exiting regardless.
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
        catch
        {
            // best-effort reconciliation - a bad tick shouldn't take down the app; the next config save retries
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
                    onTriggered: () => TriggerAsync(jobName));
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

        // The coordinator's own debounce window plus however long disposal takes to unwind the live watch loop
        // (which can block until the device's next change event, per the comment on StopAsync) leaves a real gap
        // between "user disabled the job" and "the coordinator actually stops" - re-check here so a change that
        // was already in flight when the job got disabled doesn't still trigger a run.
        if (index >= 0 && config.Jobs[index].Enabled)
            await jobRunService.RunJobAsync(index);
    }

    // Cheap "did anything this coordinator depends on change" fingerprint - avoids needing value-equality on
    // JobSchedule/JobDeviceBinding just to decide whether a watcher needs restarting.
    private static string Signature(SyncJobConfig job) => string.Join('|',
        job.Schedule.DebounceWindow, job.Schedule.RescanInterval,
        string.Join(',', job.Devices.Select(b => $"{b.DeviceName}={b.RemotePath}")));

    private sealed record ActiveWatch(ChangeWatchCoordinator Coordinator, string Signature);
}
