using AdbSync.Core.Orchestration;
using AdbSync.Core.Scheduling;

namespace AdbSync.App.Services;

/// <summary>
/// The one place that actually invokes <see cref="SyncJobRunner"/>, shared by the scheduler and manual
/// "Run Now" triggers so LastRunAt/LastSuccessAt bookkeeping only lives in one spot. Serializes runs (matches
/// the default MaxConcurrentJobs=1 - two jobs sharing a physical device would otherwise contend on one adb connection).
/// </summary>
public sealed class JobRunService(AppConfigService configService, SyncJobRunner runner)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<JobRunResult> RunJobAsync(int jobIndex, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var config = await configService.GetAsync();
            var job = config.Jobs[jobIndex];
            job.Schedule.LastRunAt = DateTimeOffset.Now;
            await configService.SaveAsync();

            var result = await runner.RunAsync(job, jobIndex, config.Devices, config.Settings, resumeFrom: null, ct);

            if (result.Outcome is JobRunOutcome.Completed or JobRunOutcome.CompletedNoChanges)
                job.Schedule.LastSuccessAt = DateTimeOffset.Now;
            await configService.SaveAsync();

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RunAllEnabledAsync(CancellationToken ct = default)
    {
        var config = await configService.GetAsync();
        for (var i = 0; i < config.Jobs.Count; i++)
        {
            if (config.Jobs[i].Enabled)
                await RunJobAsync(i, ct);
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

            var due = ScheduleCalculator.NextDueUtc(job.Schedule, now);
            if (due is not null && due <= now)
                await RunJobAsync(i, ct);
        }
    }
}
