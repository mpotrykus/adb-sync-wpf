using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Models.Devices;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Transfer;
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
    IAppRunningGuard appGuard,
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
            active.RetryCts.Cancel();
            active.RetryCts.Dispose();
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
                    _active[name].RetryCts.Cancel();
                    _active[name].RetryCts.Dispose();
                    _active.Remove(name);
                }
            }

            foreach (var job in wanted.Values)
            {
                if (_active.ContainsKey(job.Name))
                    continue;

                var jobExclude = new ExcludeMatcher(job.Exclude);
                var bindings = job.Devices
                    .Select(b => new ChangeWatchBinding(config.Devices.First(d => d.Name == b.DeviceName), b.RemotePath, jobExclude))
                    .ToList();

                var jobName = job.Name;
                // Owns only the wait-for-app-close retry below - cancelled on teardown so a job that's disabled
                // or removed while its app is still open doesn't leave a blocked adb connection behind.
                var retryCts = new CancellationTokenSource();
                var coordinator = new ChangeWatchCoordinator(
                    jobName, bindings, watcher, deviceResolver, events,
                    job.Schedule.DebounceWindow, job.Schedule.RescanInterval,
                    onTriggered: () => TriggerAsync(jobName, retryCts.Token),
                    logger: logger);
                coordinator.Start();
                _active[job.Name] = new ActiveWatch(coordinator, Signature(job), retryCts);
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    /// <summary>
    /// Waits out any app-running block *before* handing off to <see cref="JobRunService.RunJobAsync"/>, so the
    /// change → wait → sync sequence reads as one run: a single row in run history and one continuous
    /// "Waiting for app to close" → pull → push phase progression on the dashboard, instead of an immediate
    /// SkippedAppRunning row followed later by a second, unrelated-looking successful run.
    /// </summary>
    private async Task TriggerAsync(string jobName, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var config = await configService.GetAsync();
                var index = config.Jobs.FindIndex(j => j.Name == jobName);
                if (index < 0 || !config.Jobs[index].Enabled)
                    return;

                await WaitForAppToCloseAsync(jobName, config, index, ct);

                var result = await jobRunService.RunJobAsync(index, ct: ct);
                if (result.Outcome != JobRunOutcome.SkippedAppRunning)
                    return;

                // Lost a race - the app was reopened, or another bound device had it open, between our check
                // above and the run actually starting. Loop back and wait again rather than dropping the sync.
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The watch for this job was stopped/reconciled away while waiting for the app to close - reset the
            // dashboard row so it doesn't sit on "Waiting for app to close" forever with nothing to update it.
            events.JobSkipped(jobName, "watch stopped");
        }
    }

    private async Task WaitForAppToCloseAsync(string jobName, AppConfig config, int index, CancellationToken ct)
    {
        var job = config.Jobs[index];
        if (job.AppPackage is null)
            return;

        var serials = new List<string>();
        var deviceNamesBySerial = new Dictionary<string, string>();
        foreach (var binding in job.Devices)
        {
            var device = config.Devices.First(d => d.Name == binding.DeviceName);
            var serial = await deviceResolver.EnsureConnectedAsync(device, ct);
            serials.Add(serial);
            deviceNamesBySerial[serial] = binding.DeviceName;
        }

        var runningSerial = await appGuard.FindRunningSerialAsync(job.AppPackage, serials, ct);
        if (runningSerial is null)
            return;

        var deviceName = deviceNamesBySerial.GetValueOrDefault(runningSerial, runningSerial);
        events.PhaseChanged(jobName, SyncPhase.WaitingForAppClose, deviceName);

        try
        {
            await appGuard.WaitUntilStoppedAsync(job.AppPackage, runningSerial, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // adb connection dropped (device unplugged, wifi hiccup, etc) before the app closed - back off briefly
            // and let the outer loop in TriggerAsync re-check rather than spinning on a broken connection.
            logger.LogWarning(ex, "Lost the wait-for-app-close connection for job '{Job}'; retrying shortly", jobName);
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }

    private static string Signature(SyncJobConfig job) => string.Join('|',
        job.Schedule.DebounceWindow, job.Schedule.RescanInterval,
        string.Join(',', job.Devices.Select(b => $"{b.DeviceName}={b.RemotePath}")),
        string.Join(',', job.Exclude));

    private sealed record ActiveWatch(ChangeWatchCoordinator Coordinator, string Signature, CancellationTokenSource RetryCts);
}
