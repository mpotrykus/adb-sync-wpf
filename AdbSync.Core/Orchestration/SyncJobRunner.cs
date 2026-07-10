using System.Diagnostics;
using AdbSync.Core.Config;
using AdbSync.Core.Devices;
using AdbSync.Core.Logging;
using AdbSync.Core.Merge;
using AdbSync.Core.Orchestration.RunHistory;
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
    IRunHistoryStore runHistory,
    ILogger<SyncJobRunner>? logger = null)
{
    private readonly ILogger<SyncJobRunner> _logger = new RunCapturingLogger<SyncJobRunner>(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncJobRunner>.Instance);

    public async Task<JobRunResult> RunAsync(
        SyncJobConfig job, int jobIndex, IReadOnlyList<DeviceConfig> devices, GlobalSettings settings,
        SyncCheckpoint? resumeFrom, bool forcePush = false, CancellationToken ct = default)
    {
        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        using var runLog = RunLogCapture.Begin();

        var totalFilesCopied = 0;
        var totalFilesDeleted = 0;
        var totalErrors = 0;
        var totalBytesCopied = 0L;
        TimeSpan? pullDuration = null;
        TimeSpan? pushDuration = null;

        var projectsDirectory = string.IsNullOrWhiteSpace(job.ProjectDirectory) ? settings.ProjectsDirectory : job.ProjectDirectory;
        var projectRoot = Path.Combine(projectsDirectory, job.Name);
        var masterPath = Path.Combine(projectRoot, "master");

        _logger.LogInformation("Starting run for job '{Job}'", job.Name);
        _logger.LogInformation("Using {Engine} engine", transfer.GetType().Name);

        try
        {
            if (string.IsNullOrWhiteSpace(projectsDirectory))
                throw new InvalidOperationException("Projects directory is not configured - set it in Settings before running a job.");

            Directory.CreateDirectory(masterPath);

            await using var @lock = await lockManager.TryAcquireAsync(projectRoot, TimeSpan.FromHours(settings.StaleLockHours), ct);
            if (@lock is null)
            {
                _logger.LogInformation("Job '{Job}' skipped - already running", job.Name);
                events.JobSkipped(job.Name, "already running");
                return await FinishAsync(JobRunOutcome.Skipped);
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
                return await FinishAsync(JobRunOutcome.SkippedAppRunning);
            }

            var anyChange = resumeFrom is { Phase: SyncPhase.Push };
            var pullFilesTouched = 0;
            if (!anyChange)
            {
                var pullStopwatch = Stopwatch.StartNew();
                var pullStats = await RunPullPhaseAsync(job, jobIndex, masterPath, serials, exclude, resumeFrom, ct);
                pullDuration = pullStopwatch.Elapsed;
                anyChange = pullStats.AnyChange;
                totalErrors += pullStats.Errors;
                totalBytesCopied += pullStats.BytesCopied;
                pullFilesTouched = pullStats.FilesCopied + pullStats.FilesDeleted;
            }

            if (!anyChange)
            {
                _logger.LogInformation("Job '{Job}' completed - no changes", job.Name);
                events.JobCompleted(job.Name, pushed: false);
                await checkpoints.ClearAsync(ct);
                return await FinishAsync(JobRunOutcome.CompletedNoChanges);
            }

            if (forcePush)
            {
                _logger.LogWarning("Job '{Job}' push-safety check bypassed by manual override", job.Name);
                await pushSafety.ForcePushAsync(job.Name, masterPath, ct);
            }
            else
            {
                await pushSafety.AssertSafeToPushAsync(job.Name, masterPath, ct);
            }

            var pushStopwatch = Stopwatch.StartNew();
            var pushStats = await RunPushPhaseAsync(job, jobIndex, masterPath, serials, exclude, resumeFrom, ct);
            pushDuration = pushStopwatch.Elapsed;
            // Files column reports unique files actually pushed, not a per-device sum - pushing the same
            // file to 3 devices should read as "1 copied", not "3 copied".
            totalFilesCopied = pushStats.CopiedPaths.Count;
            totalFilesDeleted = pushStats.DeletedPaths.Count;
            totalErrors += pushStats.Errors;
            totalBytesCopied += pushStats.BytesCopied;

            _logger.LogInformation("Job '{Job}' completed successfully", job.Name);
            events.JobCompleted(job.Name, pushed: true);
            await checkpoints.ClearAsync(ct);
            var outcome = pullFilesTouched == 0 && totalFilesCopied == 0 && totalFilesDeleted == 0
                ? JobRunOutcome.CompletedNoChanges
                : JobRunOutcome.Completed;
            return await FinishAsync(outcome);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job '{Job}' failed", job.Name);
            events.JobFailed(job.Name, ex);
            return await FinishAsync(JobRunOutcome.Failed, ex.Message);
        }

        async Task<JobRunResult> FinishAsync(JobRunOutcome outcome, string? errorMessage = null)
        {
            var record = new JobRunRecord(
                runId, job.Name, startedAt, DateTimeOffset.UtcNow, outcome, errorMessage,
                totalFilesCopied, totalFilesDeleted, totalErrors, totalBytesCopied, pullDuration, pushDuration);
            try
            {
                await runHistory.SaveRunAsync(record, runLog.BuildText(), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save run history for '{Job}'", job.Name);
            }
            return new JobRunResult(outcome, errorMessage);
        }
    }

    private async Task<PhaseStats> RunPullPhaseAsync(
        SyncJobConfig job, int jobIndex, string masterPath, Dictionary<string, string> serials,
        ExcludeMatcher exclude, SyncCheckpoint? resumeFrom, CancellationToken ct)
    {
        var projectRoot = Path.GetDirectoryName(masterPath)!;
        var startIndex = resumeFrom is { Phase: SyncPhase.Pull } r ? r.DeviceIndex : 0;
        var anyChange = false;
        var filesCopied = 0;
        var filesDeleted = 0;
        var errors = 0;
        var bytesCopied = 0L;

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
            filesCopied += pullResult.FilesCopied;
            filesDeleted += pullResult.FilesDeleted;
            errors += pullResult.Errors.Count;
            bytesCopied += pullResult.BytesCopied;

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

        return new PhaseStats(anyChange, filesCopied, filesDeleted, errors, bytesCopied, [], []);
    }

    private async Task<PhaseStats> RunPushPhaseAsync(
        SyncJobConfig job, int jobIndex, string masterPath, Dictionary<string, string> serials,
        ExcludeMatcher exclude, SyncCheckpoint? resumeFrom, CancellationToken ct)
    {
        var startIndex = resumeFrom is { Phase: SyncPhase.Push } r ? r.DeviceIndex : 0;
        var filesCopied = 0;
        var filesDeleted = 0;
        var errors = 0;
        var bytesCopied = 0L;
        var copiedPaths = new HashSet<string>(StringComparer.Ordinal);
        var deletedPaths = new HashSet<string>(StringComparer.Ordinal);

        for (var di = startIndex; di < job.Devices.Count; di++)
        {
            var binding = job.Devices[di];
            var serial = serials[binding.DeviceName];

            events.PhaseChanged(job.Name, SyncPhase.Push, binding.DeviceName);
            var pushResult = await transfer.PushMirrorAsync(serial, masterPath, binding.RemotePath, exclude, ct);
            _logger.LogInformation(
                "Job '{Job}' pushed to '{Device}': {Copied} copied, {Deleted} deleted, {Errors} error(s)",
                job.Name, binding.DeviceName, pushResult.FilesCopied, pushResult.FilesDeleted, pushResult.Errors.Count);
            filesCopied += pushResult.FilesCopied;
            filesDeleted += pushResult.FilesDeleted;
            errors += pushResult.Errors.Count;
            bytesCopied += pushResult.BytesCopied;
            copiedPaths.UnionWith(pushResult.CopiedPaths);
            deletedPaths.UnionWith(pushResult.DeletedPaths);

            await checkpoints.SaveAsync(new SyncCheckpoint(1, DateTimeOffset.UtcNow, jobIndex, job.Name, SyncPhase.Push, di + 1, serials), ct);
        }

        return new PhaseStats(AnyChange: false, filesCopied, filesDeleted, errors, bytesCopied, copiedPaths.ToList(), deletedPaths.ToList());
    }

    private readonly record struct PhaseStats(
        bool AnyChange, int FilesCopied, int FilesDeleted, int Errors, long BytesCopied,
        IReadOnlyList<string> CopiedPaths, IReadOnlyList<string> DeletedPaths);

    private static string GetStagingPath(string projectRoot, string deviceName)
    {
        var path = Path.Combine(SyncLockManager.GetStagingRoot(projectRoot), deviceName);
        Directory.CreateDirectory(path);
        return path;
    }
}
