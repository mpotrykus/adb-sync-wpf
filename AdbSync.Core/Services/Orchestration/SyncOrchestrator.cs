using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Orchestration;

namespace AdbSync.Core.Services.Orchestration;

/// <summary>
/// Runs every enabled job sequentially (matching the old tool's fully-sequential behavior), isolating failures
/// per job so one bad job doesn't block the rest of the queue. Concurrent job execution is a future enhancement -
/// it needs a same-device-in-flight guard first, since two jobs sharing a physical device would contend on one
/// adb connection.
/// </summary>
public sealed class SyncOrchestrator(SyncJobRunner runner, ICheckpointManager checkpoints)
{
    public async Task<IReadOnlyList<JobRunResult>> RunAllAsync(AppConfig config, CancellationToken ct = default)
    {
        var results = new List<JobRunResult>();
        for (var index = 0; index < config.Jobs.Count; index++)
        {
            var job = config.Jobs[index];
            if (!job.Enabled)
                continue;

            var resume = await checkpoints.LoadAsync(job.Name, ct);
            results.Add(await runner.RunAsync(job, index, config.Devices, config.Settings, resume, forcePush: false, ct));
        }
        return results;
    }
}
