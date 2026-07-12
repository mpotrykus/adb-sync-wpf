using System.Collections.Concurrent;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Scheduling;

namespace AdbSync.App.Services;

/// <summary>
/// The one place that actually invokes <see cref="SyncJobRunner"/>, shared by the scheduler and manual
/// "Run Now" triggers so LastRunAt/LastSuccessAt bookkeeping only lives in one spot. Gates concurrency two ways:
/// a global cap sized from <see cref="GlobalSettings.MaxConcurrentJobs"/>, and a per-device-name lock so two jobs
/// sharing a physical device can never contend on the same adb connection even when the global cap allows more
/// than one job to run at once.
/// </summary>
public sealed class JobRunService(AppConfigService configService, SyncJobRunner runner, IDeviceSnapshotService snapshotService)
{
    // Sized once from settings.MaxConcurrentJobs on first use - restart to apply, the same tradeoff already
    // accepted for LogRetentionDays/PerLogFileMaxBytes at startup.
    private readonly Lazy<Task<SemaphoreSlim>> _globalGate = new(async () =>
    {
        var config = await configService.GetAsync();
        var max = Math.Max(1, config.Settings.MaxConcurrentJobs);
        return new SemaphoreSlim(max, max);
    });

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceGates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<JobRunResult> RunJobAsync(int jobIndex, bool forcePush = false, CancellationToken ct = default)
    {
        var config = await configService.GetAsync();
        var job = config.Jobs[jobIndex];
        await using var _ = await AcquireAsync(job, ct);

        job.Schedule.LastRunAt = DateTimeOffset.Now;
        await configService.SaveAsync();

        var result = await runner.RunAsync(job, jobIndex, config.Devices, config.Settings, resumeFrom: null, forcePush, ct);

        if (result.Outcome is JobRunOutcome.Completed or JobRunOutcome.CompletedNoChanges)
            job.Schedule.LastSuccessAt = DateTimeOffset.Now;
        await configService.SaveAsync();

        return result;
    }

    /// <summary>Pulls the current state of every device bound to the job into a new timestamped backup folder,
    /// without touching master/staging/devices. Shares the same gates as <see cref="RunJobAsync"/> so it can't
    /// race a sync run over the same device's adb connection.</summary>
    public async Task<SnapshotResult> CreateSnapshotAsync(int jobIndex, CancellationToken ct = default)
    {
        var config = await configService.GetAsync();
        var job = config.Jobs[jobIndex];
        await using var _ = await AcquireAsync(job, ct);
        return await snapshotService.CreateSnapshotAsync(job, config.Devices, config.Settings, ct);
    }

    /// <summary>Lists this job's stored checkpoints, most recent first. Read-only - doesn't need the gates.</summary>
    public async Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(int jobIndex, CancellationToken ct = default)
    {
        var config = await configService.GetAsync();
        var job = config.Jobs[jobIndex];
        return snapshotService.ListSnapshots(job, config.Settings);
    }

    /// <summary>Pushes a stored checkpoint back out to the job's devices. Shares the same gates as
    /// <see cref="RunJobAsync"/>/<see cref="CreateSnapshotAsync"/> so it can't race a sync run over the same
    /// device's adb connection.</summary>
    public async Task<SnapshotResult> RestoreSnapshotAsync(int jobIndex, string snapshotPath, CancellationToken ct = default)
    {
        var config = await configService.GetAsync();
        var job = config.Jobs[jobIndex];
        await using var _ = await AcquireAsync(job, ct);
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
    /// first, so two jobs sharing more than one device always acquire in the same order and can't deadlock).
    /// Dispose the result to release everything acquired.</summary>
    private async Task<IAsyncDisposable> AcquireAsync(SyncJobConfig job, CancellationToken ct)
    {
        var globalGate = await _globalGate.Value;
        await globalGate.WaitAsync(ct);

        var deviceNames = job.Devices
            .Select(d => d.DeviceName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var acquired = new List<SemaphoreSlim>();
        try
        {
            foreach (var name in deviceNames)
            {
                var gate = _deviceGates.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct);
                acquired.Add(gate);
            }
        }
        catch
        {
            foreach (var gate in acquired)
                gate.Release();
            globalGate.Release();
            throw;
        }

        return new Releaser(globalGate, acquired);
    }

    private sealed class Releaser(SemaphoreSlim globalGate, List<SemaphoreSlim> deviceGates) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            foreach (var gate in deviceGates)
                gate.Release();
            globalGate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
