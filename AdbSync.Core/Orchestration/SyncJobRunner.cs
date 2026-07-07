using AdbSync.Core.Config;
using AdbSync.Core.Devices;
using AdbSync.Core.Merge;
using AdbSync.Core.Transfer;
using Microsoft.Extensions.Logging;

namespace AdbSync.Core.Orchestration;

/// <summary>Runs one sync job's full pipeline: lock -> connect devices -> app-running guard -> pull+merge per device -> push-safety -> push per device.</summary>
public sealed class SyncJobRunner(
    IAdbDeviceResolver deviceResolver,
    IAppRunningGuard appGuard,
    ISyncLockManager lockManager,
    IAdbTransferEngine transfer,
    ITwoWayMergeEngine merge,
    IManifestStore manifests,
    IPushSafetyGuard pushSafety,
    ICheckpointManager checkpoints,
    ISyncEventSink events,
    ILogger<SyncJobRunner>? logger = null)
{
    private readonly ILogger<SyncJobRunner> _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncJobRunner>.Instance;

    public async Task<JobRunResult> RunAsync(
        SyncJobConfig job, int jobIndex, IReadOnlyList<DeviceConfig> devices, GlobalSettings settings,
        SyncCheckpoint? resumeFrom, CancellationToken ct = default)
    {
        var projectRoot = Path.Combine(settings.ProjectsDirectory, job.Name);
        var masterPath = Path.Combine(projectRoot, "master");

        _logger.LogInformation("Starting run for job '{Job}'", job.Name);

        try
        {
            if (string.IsNullOrWhiteSpace(settings.ProjectsDirectory))
                throw new InvalidOperationException("Projects directory is not configured - set it in Settings before running a job.");

            Directory.CreateDirectory(masterPath);

            await using var @lock = await lockManager.TryAcquireAsync(projectRoot, TimeSpan.FromHours(settings.StaleLockHours), ct);
            if (@lock is null)
            {
                _logger.LogInformation("Job '{Job}' skipped - already running", job.Name);
                events.JobSkipped(job.Name, "already running");
                return new JobRunResult(JobRunOutcome.Skipped);
            }

            events.PhaseChanged(job.Name, SyncPhase.PreConnect);
            var exclude = new ExcludeMatcher(job.Exclude);

            var serials = new Dictionary<string, string>();
            foreach (var binding in job.Devices)
            {
                var device = devices.FirstOrDefault(d => d.Name == binding.DeviceName)
                    ?? throw new InvalidOperationException($"Device '{binding.DeviceName}' referenced by job '{job.Name}' was not found.");
                serials[binding.DeviceName] = await deviceResolver.EnsureConnectedAsync(device, ct);
            }

            if (job.AppPackage is not null && await appGuard.IsRunningAnywhereAsync(job.AppPackage, serials.Values, ct))
            {
                _logger.LogInformation("Job '{Job}' skipped - {Package} is running on a device", job.Name, job.AppPackage);
                events.JobSkipped(job.Name, $"{job.AppPackage} is running on a device");
                return new JobRunResult(JobRunOutcome.SkippedAppRunning);
            }

            var anyChange = resumeFrom is { Phase: SyncPhase.Push };
            if (!anyChange)
                anyChange = await RunPullPhaseAsync(job, jobIndex, masterPath, serials, exclude, resumeFrom, ct);

            if (!anyChange)
            {
                _logger.LogInformation("Job '{Job}' completed - no changes", job.Name);
                events.JobCompleted(job.Name, pushed: false);
                await checkpoints.ClearAsync(ct);
                return new JobRunResult(JobRunOutcome.CompletedNoChanges);
            }

            await pushSafety.AssertSafeToPushAsync(job.Name, masterPath, ct);
            await RunPushPhaseAsync(job, jobIndex, masterPath, serials, exclude, resumeFrom, ct);

            _logger.LogInformation("Job '{Job}' completed successfully", job.Name);
            events.JobCompleted(job.Name, pushed: true);
            await checkpoints.ClearAsync(ct);
            return new JobRunResult(JobRunOutcome.Completed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job '{Job}' failed", job.Name);
            events.JobFailed(job.Name, ex);
            return new JobRunResult(JobRunOutcome.Failed, ex.Message);
        }
    }

    private async Task<bool> RunPullPhaseAsync(
        SyncJobConfig job, int jobIndex, string masterPath, Dictionary<string, string> serials,
        ExcludeMatcher exclude, SyncCheckpoint? resumeFrom, CancellationToken ct)
    {
        var projectRoot = Path.GetDirectoryName(masterPath)!;
        var startIndex = resumeFrom is { Phase: SyncPhase.Pull } r ? r.DeviceIndex : 0;
        var anyChange = false;

        for (var di = startIndex; di < job.Devices.Count; di++)
        {
            var binding = job.Devices[di];
            var serial = serials[binding.DeviceName];
            var stagingPath = GetStagingPath(projectRoot, binding.DeviceName);

            events.PhaseChanged(job.Name, SyncPhase.Pull, binding.DeviceName);
            var pullResult = await transfer.PullMirrorAsync(serial, binding.RemotePath, stagingPath, exclude, ct);
            _logger.LogInformation(
                "Job '{Job}' pulled from '{Device}': {Copied} copied, {Deleted} deleted, {Errors} error(s)",
                job.Name, binding.DeviceName, pullResult.FilesCopied, pullResult.FilesDeleted, pullResult.Errors.Count);

            var fileCount = Directory.Exists(stagingPath)
                ? Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories).Count()
                : 0;
            await pushSafety.RecordDeviceSnapshotAsync(job.Name, binding.DeviceName, fileCount, ct);

            var manifest = await manifests.GetOrBootstrapAsync(job.Name, binding.DeviceName, stagingPath, masterPath, ct);
            var mergeResult = await merge.MergeAsync(stagingPath, masterPath, manifest, new MergeOptions(), ct);
            await manifests.SaveAsync(job.Name, binding.DeviceName, mergeResult.UpdatedManifest, ct);
            anyChange |= pullResult.AnyChange || mergeResult.AnyChange;

            if (mergeResult.Conflicts.Count > 0)
            {
                _logger.LogWarning(
                    "Job '{Job}' merge with '{Device}' had {Count} conflict(s): {Paths}",
                    job.Name, binding.DeviceName, mergeResult.Conflicts.Count,
                    string.Join(", ", mergeResult.Conflicts.Select(c => c.RelativePath)));
                events.MergeConflictsDetected(job.Name, binding.DeviceName, mergeResult.Conflicts.Count);
            }

            if (Directory.Exists(stagingPath))
                Directory.Delete(stagingPath, recursive: true);

            await checkpoints.SaveAsync(new SyncCheckpoint(1, DateTimeOffset.UtcNow, jobIndex, job.Name, SyncPhase.Pull, di + 1, serials), ct);
        }

        return anyChange;
    }

    private async Task RunPushPhaseAsync(
        SyncJobConfig job, int jobIndex, string masterPath, Dictionary<string, string> serials,
        ExcludeMatcher exclude, SyncCheckpoint? resumeFrom, CancellationToken ct)
    {
        var startIndex = resumeFrom is { Phase: SyncPhase.Push } r ? r.DeviceIndex : 0;

        for (var di = startIndex; di < job.Devices.Count; di++)
        {
            var binding = job.Devices[di];
            var serial = serials[binding.DeviceName];

            events.PhaseChanged(job.Name, SyncPhase.Push, binding.DeviceName);
            var pushResult = await transfer.PushMirrorAsync(serial, masterPath, binding.RemotePath, exclude, ct);
            _logger.LogInformation(
                "Job '{Job}' pushed to '{Device}': {Copied} copied, {Deleted} deleted, {Errors} error(s)",
                job.Name, binding.DeviceName, pushResult.FilesCopied, pushResult.FilesDeleted, pushResult.Errors.Count);

            await checkpoints.SaveAsync(new SyncCheckpoint(1, DateTimeOffset.UtcNow, jobIndex, job.Name, SyncPhase.Push, di + 1, serials), ct);
        }
    }

    private static string GetStagingPath(string projectRoot, string deviceName)
    {
        var path = Path.Combine(SyncLockManager.GetStagingRoot(projectRoot), deviceName);
        Directory.CreateDirectory(path);
        return path;
    }
}
