using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Merge;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using AdbSync.Core.Models.Transfer;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Services.Orchestration.RunHistory;
using AdbSync.Core.Services.Transfer;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AdbSync.Core.Services.Orchestration;

/// <summary>Runs one sync job's full pipeline: lock -> connect devices -> app-running guard -> pull+merge per
/// device (concurrently, one device's busy wait never blocks another's) -> push-safety -> push per device
/// (also concurrently). Each device's gate (up to GlobalSettings.MaxConcurrentPerDevice concurrent holders) is
/// held only for the duration of its actual pull/push call, not across the whole run - merge/manifest/checkpoint
/// bookkeeping is local disk I/O and needs no device, so another job can use a device the moment this job's
/// transfer with it finishes.</summary>
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
    ILogger<SyncJobRunner>? logger = null,
    ILiveRunLogSink? liveLog = null,
    IDeviceAccessGate? deviceAccessGate = null)
{
    private readonly ILogger<SyncJobRunner> _logger = new RunCapturingLogger<SyncJobRunner>(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncJobRunner>.Instance);
    private readonly IDeviceAccessGate _deviceAccessGate = deviceAccessGate ?? new DeviceAccessGate();

    public async Task<JobRunResult> RunAsync(
        SyncJobConfig job, int jobIndex, IReadOnlyList<DeviceConfig> devices, GlobalSettings settings,
        SyncCheckpoint? resumeFrom, bool forcePush = false, CancellationToken ct = default)
    {
        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        using var runLog = RunLogCapture.Begin(job.Name, liveLog);

        var totalFilesCopied = 0;
        var totalFilesDeleted = 0;
        var totalErrors = 0;
        var totalBytesCopied = 0L;
        TimeSpan? pullDuration = null;
        TimeSpan? pushDuration = null;

        var eff = job.Resolve(settings);
        if (eff.DryRun)
            resumeFrom = null;

        var projectsDirectory = eff.ProjectsDirectory;
        var projectRoot = Path.Combine(projectsDirectory, job.Name);
        var masterPath = Path.Combine(projectRoot, "master");

        _logger.LogInformation("Starting run for job '{Job}'", job.Name);
        _logger.LogInformation("Using {Engine} engine", transfer.GetType().Name);

        try
        {
            if (string.IsNullOrWhiteSpace(projectsDirectory))
                throw new InvalidOperationException("Projects directory is not configured - set it in Settings before running a job.");

            Directory.CreateDirectory(masterPath);

            await using var @lock = await lockManager.TryAcquireAsync(projectRoot, TimeSpan.FromHours(eff.StaleLockHours), ct);
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

            if (job.AppPackage is not null)
            {
                var runningSerial = await appGuard.FindRunningSerialAsync(job.AppPackage, serials.Values, ct);
                if (runningSerial is not null)
                {
                    var deviceName = serials.FirstOrDefault(kv => kv.Value == runningSerial).Key ?? runningSerial;
                    _logger.LogInformation("Job '{Job}' skipped - {Package} is currently running on {Device}", job.Name, job.AppPackage, deviceName);
                    events.JobSkipped(job.Name, $"{job.AppPackage} is currently running on {deviceName}");
                    return await FinishAsync(JobRunOutcome.SkippedAppRunning);
                }
            }

            var anyChange = resumeFrom is { Phase: SyncPhase.Push };
            if (!anyChange)
            {
                var pullStopwatch = Stopwatch.StartNew();
                var pullStats = await RunPullPhaseAsync(job, jobIndex, masterPath, serials, exclude, resumeFrom, eff, settings.MaxConcurrentPerDevice, ct);
                pullDuration = pullStopwatch.Elapsed;
                anyChange = pullStats.AnyChange;
                totalErrors += pullStats.Errors;
                totalBytesCopied += pullStats.BytesCopied;
            }

            if (eff.DryRun)
            {
                _logger.LogInformation("Job '{Job}' dry run completed - nothing was written to master or pushed", job.Name);
                events.JobCompleted(job.Name, pushed: false);
                return await FinishAsync(JobRunOutcome.DryRunCompleted);
            }

            if (!anyChange)
            {
                _logger.LogInformation("Job '{Job}' completed - no changes", job.Name);
                events.JobCompleted(job.Name, pushed: false);
                await checkpoints.ClearAsync(job.Name, ct);
                return await FinishAsync(JobRunOutcome.CompletedNoChanges);
            }

            if (forcePush)
            {
                _logger.LogWarning("Job '{Job}' push-safety check bypassed by manual override", job.Name);
                await pushSafety.ForcePushAsync(job.Name, masterPath, ct);
            }
            else
            {
                await pushSafety.AssertSafeToPushAsync(job.Name, masterPath, eff.PushSafetyMinimumPercent, ct);
            }

            var pushStopwatch = Stopwatch.StartNew();
            var pushStats = await RunPushPhaseAsync(job, jobIndex, masterPath, serials, exclude, resumeFrom, eff, settings.MaxConcurrentPerDevice, ct);
            pushDuration = pushStopwatch.Elapsed;
            totalFilesCopied = pushStats.CopiedPaths.Count;
            totalFilesDeleted = pushStats.DeletedPaths.Count;
            totalErrors += pushStats.Errors;
            totalBytesCopied += pushStats.BytesCopied;

            _logger.LogInformation("Job '{Job}' completed successfully", job.Name);
            events.JobCompleted(job.Name, pushed: true);
            await checkpoints.ClearAsync(job.Name, ct);
            var outcome = totalFilesCopied == 0 && totalFilesDeleted == 0
                ? JobRunOutcome.CompletedNoChanges
                : JobRunOutcome.Completed;
            return await FinishAsync(outcome);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Job '{Job}' stopped by user request", job.Name);
            events.JobCancelled(job.Name);
            return await FinishAsync(JobRunOutcome.Cancelled);
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
                await runHistory.SaveRunAsync(record, runLog.BuildText(), eff.MaxRunHistoryEntries, CancellationToken.None);
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
        ExcludeMatcher exclude, SyncCheckpoint? resumeFrom, EffectiveJobSettings eff, int maxConcurrentPerDevice,
        CancellationToken ct)
    {
        var projectRoot = Path.GetDirectoryName(masterPath)!;
        var completed = resumeFrom is { Phase: SyncPhase.Pull } r ? new List<string>(r.CompletedDevices) : [];
        var alreadyDone = new HashSet<string>(completed, StringComparer.OrdinalIgnoreCase);
        var pending = job.Devices.Where(b => !alreadyDone.Contains(b.DeviceName)).ToList();

        var anyChange = false;
        var filesCopied = 0;
        var filesDeleted = 0;
        var errors = 0;
        var bytesCopied = 0L;
        using var bookkeepingGate = new SemaphoreSlim(1, 1);

        async Task ProcessDeviceAsync(JobDeviceBinding binding)
        {
            var serial = serials[binding.DeviceName];
            var stagingPath = GetStagingPath(projectRoot, binding.DeviceName);

            if (_deviceAccessGate.IsBusy(binding.DeviceName))
                events.PhaseChanged(job.Name, SyncPhase.WaitingForDevice, binding.DeviceName);
            TransferResult pullResult;
            await using (await _deviceAccessGate.AcquireAsync(binding.DeviceName, maxConcurrentPerDevice, ct))
            {
                events.PhaseChanged(job.Name, SyncPhase.Pull, binding.DeviceName);
                _logger.LogInformation("Job '{Job}' pulling from '{Device}'", job.Name, binding.DeviceName);
                pullResult = await transfer.PullMirrorAsync(serial, binding.RemotePath, stagingPath, exclude, eff.ToTransferPolicy(), ct);
            }
            _logger.LogInformation(
                "Job '{Job}' pulled from '{Device}': {Copied} copied, {Deleted} deleted, {Errors} error(s)",
                job.Name, binding.DeviceName, pullResult.FilesCopied, pullResult.FilesDeleted, pullResult.Errors.Count);

            var fileCount = Directory.Exists(stagingPath)
                ? Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories).Count()
                : 0;

            var manifest = await manifests.GetOrBootstrapAsync(job.Name, binding.DeviceName, stagingPath, masterPath, ct);
            var mergeOptions = new MergeOptions(
                BackupConflictLosers: eff.BackupConflictLosers,
                ConflictBackupDir: GetConflictBackupDir(projectRoot, binding.DeviceName),
                DryRun: eff.DryRun);

            await bookkeepingGate.WaitAsync(ct);
            try
            {
                filesCopied += pullResult.FilesCopied;
                filesDeleted += pullResult.FilesDeleted;
                errors += pullResult.Errors.Count;
                bytesCopied += pullResult.BytesCopied;

                await pushSafety.RecordDeviceSnapshotAsync(job.Name, binding.DeviceName, fileCount, ct);

                events.PhaseChanged(job.Name, SyncPhase.Merge, binding.DeviceName);
                _logger.LogInformation("Job '{Job}' merge with '{Device}' started", job.Name, binding.DeviceName);
                var mergeResult = await merge.MergeAsync(stagingPath, masterPath, manifest, mergeOptions, ct);
                if (!eff.DryRun)
                    await manifests.SaveAsync(job.Name, binding.DeviceName, mergeResult.UpdatedManifest, ct);
                anyChange |= pullResult.AnyChange || mergeResult.AnyChange;
                _logger.LogInformation(
                    "Job '{Job}' merge with '{Device}' completed: {Created} created, {Updated} updated, {Deleted} deleted, {Conflicts} conflict(s)",
                    job.Name, binding.DeviceName, mergeResult.Created, mergeResult.Updated, mergeResult.Deleted, mergeResult.Conflicts.Count);

                if (mergeResult.Conflicts.Count > 0)
                {
                    _logger.LogWarning(
                        "Job '{Job}' merge with '{Device}' had {Count} conflict(s): {Paths}",
                        job.Name, binding.DeviceName, mergeResult.Conflicts.Count,
                        string.Join(", ", mergeResult.Conflicts.Select(c => c.RelativePath)));
                    events.MergeConflictsDetected(job.Name, binding.DeviceName, mergeResult.Conflicts.Count);
                }

                PruneConflictBackups(mergeOptions.ConflictBackupDir!, eff.ConflictRetentionDays);

                if (Directory.Exists(stagingPath))
                    Directory.Delete(stagingPath, recursive: true);

                completed.Add(binding.DeviceName);
                if (!eff.DryRun)
                    await checkpoints.SaveAsync(job.Name, new SyncCheckpoint(1, DateTimeOffset.UtcNow, jobIndex, job.Name, SyncPhase.Pull, completed.ToList(), serials), ct);
            }
            finally
            {
                bookkeepingGate.Release();
            }
        }

        await Task.WhenAll(pending.Select(ProcessDeviceAsync));

        return new PhaseStats(anyChange, filesCopied, filesDeleted, errors, bytesCopied, [], []);
    }

    private async Task<PhaseStats> RunPushPhaseAsync(
        SyncJobConfig job, int jobIndex, string masterPath, Dictionary<string, string> serials,
        ExcludeMatcher exclude, SyncCheckpoint? resumeFrom, EffectiveJobSettings eff, int maxConcurrentPerDevice,
        CancellationToken ct)
    {
        var completed = resumeFrom is { Phase: SyncPhase.Push } r ? new List<string>(r.CompletedDevices) : [];
        var alreadyDone = new HashSet<string>(completed, StringComparer.OrdinalIgnoreCase);
        var pending = job.Devices.Where(b => !alreadyDone.Contains(b.DeviceName)).ToList();

        var filesCopied = 0;
        var filesDeleted = 0;
        var errors = 0;
        var bytesCopied = 0L;
        var copiedPaths = new HashSet<string>(StringComparer.Ordinal);
        var deletedPaths = new HashSet<string>(StringComparer.Ordinal);
        using var bookkeepingGate = new SemaphoreSlim(1, 1);

        async Task ProcessDeviceAsync(JobDeviceBinding binding)
        {
            var serial = serials[binding.DeviceName];

            if (_deviceAccessGate.IsBusy(binding.DeviceName))
                events.PhaseChanged(job.Name, SyncPhase.WaitingForDevice, binding.DeviceName);
            TransferResult pushResult;
            await using (await _deviceAccessGate.AcquireAsync(binding.DeviceName, maxConcurrentPerDevice, ct))
            {
                events.PhaseChanged(job.Name, SyncPhase.Push, binding.DeviceName);
                _logger.LogInformation("Job '{Job}' pushing to '{Device}'", job.Name, binding.DeviceName);
                pushResult = await transfer.PushMirrorAsync(serial, masterPath, binding.RemotePath, exclude, eff.ToTransferPolicy(), ct);
            }
            _logger.LogInformation(
                "Job '{Job}' pushed to '{Device}': {Copied} copied, {Deleted} deleted, {Errors} error(s)",
                job.Name, binding.DeviceName, pushResult.FilesCopied, pushResult.FilesDeleted, pushResult.Errors.Count);

            await bookkeepingGate.WaitAsync(ct);
            try
            {
                filesCopied += pushResult.FilesCopied;
                filesDeleted += pushResult.FilesDeleted;
                errors += pushResult.Errors.Count;
                bytesCopied += pushResult.BytesCopied;
                copiedPaths.UnionWith(pushResult.CopiedPaths);
                deletedPaths.UnionWith(pushResult.DeletedPaths);

                completed.Add(binding.DeviceName);
                await checkpoints.SaveAsync(job.Name, new SyncCheckpoint(1, DateTimeOffset.UtcNow, jobIndex, job.Name, SyncPhase.Push, completed.ToList(), serials), ct);
            }
            finally
            {
                bookkeepingGate.Release();
            }
        }

        await Task.WhenAll(pending.Select(ProcessDeviceAsync));

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

    private static string GetConflictBackupDir(string projectRoot, string deviceName) =>
        Path.Combine(projectRoot, ".sync_conflicts", deviceName);

    private static void PruneConflictBackups(string backupDir, int retentionDays)
    {
        if (retentionDays <= 0 || !Directory.Exists(backupDir))
            return;

        var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-retentionDays).UtcDateTime;
        foreach (var file in Directory.EnumerateFiles(backupDir))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                File.Delete(file);
        }
    }
}
