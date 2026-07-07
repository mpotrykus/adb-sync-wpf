using AdbSync.Core.Config;

namespace AdbSync.Core.Orchestration;

/// <summary>
/// Runs every enabled job sequentially (matching the old tool's fully-sequential behavior), isolating failures
/// per job so one bad job doesn't block the rest of the queue. Concurrent job execution is a future enhancement -
/// it needs a same-device-in-flight guard first, since two jobs sharing a physical device would contend on one
/// adb connection.
/// </summary>
public sealed class SyncOrchestrator(SyncJobRunner runner)
{
    public async Task<IReadOnlyList<JobRunResult>> RunAllAsync(AppConfig config, SyncCheckpoint? resumeFrom, CancellationToken ct = default)
    {
        var results = new List<JobRunResult>();
        for (var index = 0; index < config.Jobs.Count; index++)
        {
            var job = config.Jobs[index];
            if (!job.Enabled)
                continue;

            var resume = resumeFrom is not null && resumeFrom.ProjectIndex == index ? resumeFrom : null;
            results.Add(await runner.RunAsync(job, index, config.Devices, config.Settings, resume, ct));
        }
        return results;
    }
}
