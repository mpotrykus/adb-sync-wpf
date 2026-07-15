using System.Collections.Concurrent;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Models.Devices;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Transfer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

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

    // One entry per job currently inside TriggerAsync (waiting for its app to close, or already handed off to
    // JobRunService). Lets the dashboard's Stop button interrupt a job stuck waiting for its app to close, which
    // JobRunService.CancelJob can't do since that job hasn't been registered there yet at that point.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _manualStops = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        configService.ConfigChanged += OnConfigChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
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
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        await base.StopAsync(cancellationToken);

        await TeardownActiveAsync(cancellationToken);
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

    /// <summary>
    /// A live inotifyd stream, a wait-for-app-close shell command, or an in-flight sync run left holding an ADB
    /// connection open across a suspend can leave the network adapter unable to enter its low-power state in
    /// time, crashing the machine. Tear everything down right before suspend and rebuild the watches from config
    /// on resume, so nothing is ever asked to survive the transition. Runs synchronously (blocking the
    /// SystemEvents callback thread, not the UI thread) so the OS doesn't proceed with the suspend before the
    /// connections actually close.
    /// </summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                logger.LogInformation("System is suspending; stopping {Count} active change watch(es)", _active.Count);
                RunBlocking(() => TeardownActiveAsync(CancellationToken.None), "suspend");
                break;
            case PowerModes.Resume:
                logger.LogInformation("System resumed; restarting change watchers");
                RunBlocking(() => ReconcileAsync(CancellationToken.None), "resume");
                break;
        }
    }

    /// <summary>
    /// Same reasoning as <see cref="OnPowerModeChanged"/>, but for a Windows shutdown/restart/logoff instead of a
    /// suspend - unlike suspend, WPF does not translate this broadcast into a graceful <c>OnExit</c> for a
    /// tray-resident app with no visible window, so without this the process would simply be killed with its adb
    /// connections still open.
    /// </summary>
    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        logger.LogInformation("System session ending ({Reason}); stopping {Count} active change watch(es)", e.Reason, _active.Count);
        RunBlocking(() => TeardownActiveAsync(CancellationToken.None), "session ending");
    }

    /// <summary>Runs an async teardown/reconcile step to completion before returning to the OS notification -
    /// fire-and-forget wouldn't give the OS any reason to wait for the connections to actually close.</summary>
    private void RunBlocking(Func<Task> action, string context)
    {
        try
        {
            Task.Run(action).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to handle {Context}", context);
        }
    }

    private async Task TeardownActiveAsync(CancellationToken ct)
    {
        // Stop any in-flight sync run first - it may be holding its own adb connection mid-transfer, independent
        // of the watch connections torn down below.
        jobRunService.CancelAllRunning();

        await _reconcileGate.WaitAsync(CancellationToken.None);
        try
        {
            foreach (var (jobName, active) in _active)
            {
                active.RetryCts.Cancel();
                active.RetryCts.Dispose();

                // Capped independently of the caller's token: on suspend/shutdown there is no shutdown deadline
                // to inherit, and we want the adb connection closed quickly either way.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await active.Coordinator.DisposeAsync().AsTask().WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Deadline hit while a watch loop was still unwinding - abandon it rather than block
                    // shutdown/suspend any further.
                    logger.LogWarning("Watch for job '{Job}' didn't stop within the deadline; abandoning it", jobName);
                }
            }
            _active.Clear();
        }
        finally
        {
            _reconcileGate.Release();
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

                // The watch only reacts to changes from here forward - it won't notice anything that happened
                // while the job was disabled (or under its old config). Run once now so enabling/reconfiguring
                // an OnChange job always leaves it caught up, instead of sitting stale until the next real change.
                _ = TriggerAsync(jobName, retryCts.Token);
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
        using var manualStopCts = new CancellationTokenSource();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, manualStopCts.Token);
        _manualStops[jobName] = manualStopCts;
        try
        {
            while (true)
            {
                var config = await configService.GetAsync();
                var index = config.Jobs.FindIndex(j => j.Name == jobName);
                if (index < 0 || !config.Jobs[index].Enabled)
                    return;

                await WaitForAppToCloseAsync(jobName, config, index, cts.Token);

                var result = await jobRunService.RunJobAsync(index, ct: cts.Token);
                if (result.Outcome != JobRunOutcome.SkippedAppRunning)
                    return;

                // Lost a race - the app was reopened, or another bound device had it open, between our check
                // above and the run actually starting. Loop back and wait again rather than dropping the sync.
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (manualStopCts.IsCancellationRequested)
                // The user hit Stop while this job was waiting for its app to close (or still queued behind a
                // concurrency gate) - it never reached JobRunService.RunJobAsync, so report it the same way a
                // mid-run stop does rather than as a watch teardown.
                events.JobCancelled(jobName);
            else
                // The watch for this job was stopped/reconciled away while waiting for the app to close - reset
                // the dashboard row so it doesn't sit on "Waiting for app to close" forever with nothing to update it.
                events.JobSkipped(jobName, "watch stopped");
        }
        finally
        {
            _manualStops.TryRemove(new KeyValuePair<string, CancellationTokenSource>(jobName, manualStopCts));
        }
    }

    /// <summary>Interrupts a job that's currently waiting for its app to close (or queued behind a concurrency
    /// gate) before an OnChange-triggered run starts. Returns false if the job has no active trigger cycle right
    /// now - typically because it's not an OnChange job, or its run has already progressed far enough to be
    /// registered with <see cref="JobRunService.CancelJob"/> instead.</summary>
    public bool CancelJob(string jobName)
    {
        if (!_manualStops.TryGetValue(jobName, out var cts))
            return false;

        cts.Cancel();
        return true;
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
