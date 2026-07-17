using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Scheduling;
using System.Collections.Concurrent;

namespace AdbSync.App.Services;

/// <summary>
/// The one place that actually invokes <see cref="SyncJobRunner"/>, shared by the scheduler and manual
/// "Run Now" triggers so LastRunAt/LastSuccessAt bookkeeping only lives in one spot. Gates concurrency two ways:
/// a global cap sized from <see cref="GlobalSettings.MaxConcurrentJobs"/>, enforced here, and a per-device-name
/// gate (<see cref="IDeviceAccessGate"/>, shared with <see cref="SyncJobRunner"/>, sized from
/// <see cref="GlobalSettings.MaxConcurrentPerDevice"/>) so no more than that many things sharing a physical
/// device contend on its adb connection at once. <see cref="SyncJobRunner"/> acquires its own device gates
/// internally (one per device, concurrently, so one busy device never stalls the others) - this class only
/// takes device gates itself for snapshot/restore, which don't go through the runner.
/// </summary>
public sealed class JobRunService(
    AppConfigService configService, SyncJobRunner runner, IDeviceSnapshotService snapshotService,
    ICheckpointManager checkpoints, IDeviceAccessGate deviceAccessGate, ISyncEventSink events)
{
    // Sized once from settings.MaxConcurrentJobs on first use - restart to apply, the same tradeoff already
    // accepted for LogRetentionDays/PerLogFileMaxBytes at startup.
    private readonly Lazy<Task<SemaphoreSlim>> _globalGate = new(async () =>
    {
        var config = await configService.GetAsync();
        var max = Math.Max(1, config.Settings.MaxConcurrentJobs);
        return new SemaphoreSlim(max, max);
    });

    // Keyed by job index so the dashboard's Stop button can cancel a specific in-flight run without knowing
    // anything about how it was started (manual "Run Now", scheduler, or change-watch trigger).
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _runningJobs = new();

    public async Task<JobRunResult> RunJobAsync(int jobIndex, bool forcePush = false, CancellationToken ct = default)
    {
        var config = await configService.GetAsync();
        var job = config.Jobs[jobIndex];
        var globalGate = await _globalGate.Value;

        // Registered before the gate wait (not after, like the rest of this method's bookkeeping) so Stop can
        // cancel a job that's still queued behind the concurrency cap, not just one already handed off to the runner.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runningJobs[jobIndex] = cts;
        try
        {
            // Only reported if a slot isn't free immediately, so the common case (a slot is open) never
            // flashes a "waiting" message before the real PreConnect phase takes over.
            if (!await globalGate.WaitAsync(0, cts.Token))
            {
                events.JobQueued(job.Name, "Waiting to start - max concurrent jobs limit reached");
                await globalGate.WaitAsync(cts.Token);
            }

            try
            {
                job.Schedule.LastRunAt = DateTimeOffset.Now;
                await configService.SaveAsync();

                var resume = await checkpoints.LoadAsync(job.Name, cts.Token);
                var result = await runner.RunAsync(job, jobIndex, config.Devices, config.Settings, resume, forcePush, cts.Token);

                if (result.Outcome is JobRunOutcome.Completed or JobRunOutcome.CompletedNoChanges)
                    job.Schedule.LastSuccessAt = DateTimeOffset.Now;
                await configService.SaveAsync();

                return result;
            }
            finally
            {
                globalGate.Release();
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Only reachable while still queued - once runner.RunAsync is running, it catches its own
            // cancellation internally and reports JobCancelled itself, so this never double-reports that case.
            events.JobCancelled(job.Name);
            return new JobRunResult(JobRunOutcome.Cancelled, null);
        }
        finally
        {
            _runningJobs.TryRemove(new KeyValuePair<int, CancellationTokenSource>(jobIndex, cts));
        }
    }

    /// <summary>Requests that the given job's in-flight run stop at its next safe point. Returns false if the
    /// job isn't currently running (e.g. it already finished, or was never started).</summary>
    public bool CancelJob(int jobIndex)
    {
        if (!_runningJobs.TryGetValue(jobIndex, out var cts))
            return false;

        cts.Cancel();
        return true;
    }

    /// <summary>Requests every currently in-flight run stop at its next safe point - same mechanism as
    /// <see cref="CancelJob"/>, just applied to all of them. Used when the system is suspending or shutting
    /// down: an adb connection an in-flight run is holding open needs to close before the transition completes,
    /// and each cancelled run's checkpoint (already saved at the last completed device boundary) lets it resume
    /// cleanly on its next trigger.</summary>
    public void CancelAllRunning()
    {
        foreach (var cts in _runningJobs.Values)
            cts.Cancel();
    }

    /// <summary>Every job with a saved checkpoint (i.e. interrupted mid-run and not yet resumed to completion),
    /// for hydrating the dashboard's checkpoint badge on load.</summary>
    public Task<IReadOnlyList<SyncCheckpoint>> GetAllCheckpointsAsync(CancellationToken ct = default) =>
        checkpoints.LoadAllAsync(ct);

    /// <summary>The given job's saved checkpoint, if any - null once it's completed a run without interruption.</summary>
    public Task<SyncCheckpoint?> GetCheckpointAsync(string jobName, CancellationToken ct = default) =>
        checkpoints.LoadAsync(jobName, ct);

    /// <summary>Discards a job's saved checkpoint so its next run starts over from the beginning instead of
    /// resuming. Safe to call whether or not a checkpoint actually exists.</summary>
    public Task DiscardCheckpointAsync(string jobName, CancellationToken ct = default) =>
        checkpoints.ClearAsync(jobName, ct);

    /// <summary>Pulls the current state of every device bound to the job into a new timestamped backup folder,
    /// without touching master/staging/devices. Shares the same device gates as <see cref="SyncJobRunner"/> so it
    /// can't race a sync run over the same device's adb connection.</summary>
    public async Task<SnapshotResult> CreateSnapshotAsync(int jobIndex, CancellationToken ct = default)
    {
        var config = await configService.GetAsync();
        var job = config.Jobs[jobIndex];
        await using var _ = await AcquireDevicesAsync(job, config.Settings.MaxConcurrentPerDevice, ct);
        return await snapshotService.CreateSnapshotAsync(job, config.Devices, config.Settings, ct);
    }

    /// <summary>Lists this job's stored checkpoints, most recent first. Read-only - doesn't need the gates.</summary>
    public async Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(int jobIndex, CancellationToken ct = default)
    {
        var config = await configService.GetAsync();
        var job = config.Jobs[jobIndex];
        return snapshotService.ListSnapshots(job, config.Settings);
    }

    /// <summary>Pushes a stored checkpoint back out to the job's devices. Shares the same device gates as
    /// <see cref="CreateSnapshotAsync"/> so it can't race a sync run over the same device's adb connection.</summary>
    public async Task<SnapshotResult> RestoreSnapshotAsync(int jobIndex, string snapshotPath, CancellationToken ct = default)
    {
        var config = await configService.GetAsync();
        var job = config.Jobs[jobIndex];
        await using var _ = await AcquireDevicesAsync(job, config.Settings.MaxConcurrentPerDevice, ct);
        return await snapshotService.RestoreSnapshotAsync(job, config.Devices, config.Settings, snapshotPath, ct);
    }

    public async Task RunAllEnabledAsync(CancellationToken ct = default)
    {
        var config = await configService.GetAsync();
        for (var i = 0; i < config.Jobs.Count; i++)
        {
            if (config.Jobs[i].Enabled)
                await RunJobAsync(i, ct: ct);
        }
    }

    public async Task RunDueJobsAsync(CancellationToken ct = default)
    {
        var config = await configService.GetAsync();
        var now = DateTimeOffset.Now;
        for (var i = 0; i < config.Jobs.Count; i++)
        {
            var job = config.Jobs[i];
            if (!job.Enabled)
                continue;

            var due = ScheduleCalculator.NextDueUtc(job.Schedule, now, config.Settings.RunMissedSchedules);
            if (due is not null && due <= now)
                await RunJobAsync(i, ct: ct);
        }
    }

    /// <summary>Acquires the global concurrency cap plus one permit per device this job touches (sorted by name
    /// first, so two overlapping snapshot/restore calls always acquire in the same order and can't deadlock -
    /// unlike <see cref="SyncJobRunner"/>, this does its device acquisition in one sequential pass rather than
    /// one task per device, since snapshot/restore are rare, whole-operation actions rather than the hot path a
    /// busy device shouldn't be allowed to stall). Dispose the result to release everything acquired.</summary>
    private async Task<IAsyncDisposable> AcquireDevicesAsync(SyncJobConfig job, int maxConcurrentPerDevice, CancellationToken ct)
    {
        var globalGate = await _globalGate.Value;
        await globalGate.WaitAsync(ct);

        var deviceNames = job.Devices
            .Select(d => d.DeviceName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var acquired = new List<IAsyncDisposable>();
        try
        {
            foreach (var name in deviceNames)
                acquired.Add(await deviceAccessGate.AcquireAsync(name, maxConcurrentPerDevice, ct));
        }
        catch
        {
            foreach (var handle in acquired)
                await handle.DisposeAsync();
            globalGate.Release();
            throw;
        }

        return new Releaser(globalGate, acquired);
    }

    private sealed class Releaser(SemaphoreSlim globalGate, List<IAsyncDisposable> deviceHandles) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var handle in deviceHandles)
                await handle.DisposeAsync();
            globalGate.Release();
        }
    }
}
