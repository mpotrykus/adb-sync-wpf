using AdbSync.Core.Services.Common;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Devices;
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
    IDeviceAccessGate? deviceAccessGate = null,
    IPendingConflictStore? pendingConflicts = null,
    ILiveRunStepSink? liveSteps = null,
    RollbackPointStore? rollbackPoints = null)
{
    public const string AlreadyRunningReason = "already running";

    private readonly ILogger<SyncJobRunner> _logger = new RunCapturingLogger<SyncJobRunner>(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncJobRunner>.Instance);
    private readonly IDeviceAccessGate _deviceAccessGate = deviceAccessGate ?? new DeviceAccessGate();

    public async Task<JobRunResult> RunAsync(
        SyncJobConfig job, int jobIndex, IReadOnlyList<DeviceConfig> devices, GlobalSettings settings,
        SyncCheckpoint? resumeFrom, bool forcePush = false, CancellationToken ct = default, bool dryRun = false, Guid? runId = null)
    {
        var id = runId ?? Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        using var runLog = RunLogCapture.Begin(job.Name, liveLog);
        var steps = new RunStepRecorder(job.Name, liveSteps);

        var totalFilesCopied = 0;
        var totalFilesDeleted = 0;
        var totalErrors = 0;
        var totalBytesCopied = 0L;
        TimeSpan? pullDuration = null;
        TimeSpan? pushDuration = null;

        var eff = job.Resolve(settings);
        if (dryRun)
            eff = eff with { DryRun = true };
        if (eff.DryRun)
            resumeFrom = null;

        RollbackSession? rollback = null;

        _logger.LogInformation("Starting run for job '{Job}'", job.Name);
        _logger.LogInformation("Using {Engine} engine", transfer.GetType().Name);

        try
        {
            var projectRoot = ProjectFolders.ProjectRoot(eff, job.Name);
            var masterPath = Path.Combine(projectRoot, "master");
            foreach (var binding in job.Devices)
                ConfigNames.Segment(binding.DeviceName);

            if (ProjectFolders.Unavailable(eff, job.Name) is { } unavailable)
            {
                _logger.LogWarning("Job '{Job}' skipped - {Reason}", job.Name, unavailable);
                events.JobSkipped(job.Name, unavailable);
                return await FinishAsync(JobRunOutcome.SkippedProjectFolderUnavailable, unavailable, RunErrorCode.ProjectFolderUnavailable);
            }

            if (rollbackPoints is not null && eff.RollbackPointsToKeep > 0 && !eff.DryRun && resumeFrom is null)
                rollback = new RollbackSession(rollbackPoints, projectRoot, masterPath, eff.RollbackPointsToKeep, _logger);

            Directory.CreateDirectory(masterPath);

            await using var @lock = await lockManager.TryAcquireAsync(projectRoot, ct);
            if (@lock is null)
            {
                _logger.LogInformation("Job '{Job}' skipped - already running", job.Name);
                events.JobSkipped(job.Name, AlreadyRunningReason);
                return await FinishAsync(JobRunOutcome.Skipped, errorCode: RunErrorCode.AlreadyRunning);
            }

            steps.Plan(job.Devices.Select(b => b.DeviceName));
            events.RunStarted(job.Name, eff.DryRun);
            events.PhaseChanged(job.Name, SyncPhase.PreConnect);
            var exclude = new ExcludeMatcher(job.Exclude);

            var serials = new Dictionary<string, string>();
            foreach (var binding in job.Devices)
            {
                var device = devices.FirstOrDefault(d => d.Name == binding.DeviceName)
                    ?? throw new InvalidOperationException($"Device '{binding.DeviceName}' referenced by job '{job.Name}' was not found.");
                steps.Start(binding.DeviceName, SyncPhase.PreConnect);
                serials[binding.DeviceName] = await deviceResolver.EnsureConnectedAsync(device, ct);
                steps.Finish(binding.DeviceName, SyncPhase.PreConnect);
            }

            if (job.AppPackage is not null)
            {
                var runningSerial = await appGuard.FindRunningSerialAsync(job.AppPackage, serials.Values, ct);
                if (runningSerial is not null)
                {
                    var deviceName = serials.FirstOrDefault(kv => kv.Value == runningSerial).Key ?? runningSerial;
                    _logger.LogInformation("Job '{Job}' skipped - {Package} is currently running on {Device}", job.Name, job.AppPackage, deviceName);
                    events.JobBlockedByApp(job.Name, deviceName);
                    events.JobSkipped(job.Name, $"{job.AppPackage} is currently running on {deviceName}");
                    return await FinishAsync(JobRunOutcome.SkippedAppRunning, errorCode: RunErrorCode.AppRunning);
                }
            }

            var pendingManifests = new Dictionary<string, SyncManifest>(StringComparer.OrdinalIgnoreCase);
            var unpulled = resumeFrom?.UnpulledPaths is { } carried
                ? new Dictionary<string, List<string>>(carried, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var resumingPush = resumeFrom is { Phase: SyncPhase.Push };
            var anyChange = resumingPush;
            if (!resumingPush)
            {
                var masterIsEmpty = !Directory.EnumerateFiles(masterPath, "*", SearchOption.AllDirectories).Any();
                if (masterIsEmpty && await manifests.AnyEntriesAsync(job.Name, ct))
                {
                    if (eff.DryRun)
                    {
                        _logger.LogWarning("Job '{Job}' master is empty but manifests record files - a real run would discard them", job.Name);
                    }
                    else
                    {
                        var cleared = await manifests.ClearAsync(job.Name, ct);
                        _logger.LogWarning(
                            "Job '{Job}' master is empty but {Count} manifest(s) record previously synced files - discarded them so the missing master is not treated as deletions",
                            job.Name, cleared);
                    }
                }

                var currentRoot = new ManifestRoot(ProjectFolders.ReadRootId(eff.ProjectsDirectory), masterPath);
                var recordedRoot = await manifests.GetRootAsync(job.Name, ct);
                if (!masterIsEmpty && recordedRoot is not null && recordedRoot.RootId != currentRoot.RootId && await manifests.AnyEntriesAsync(job.Name, ct))
                {
                    var reason =
                        $"This job last synced against a different projects folder ('{recordedRoot.MasterPath}'), so its sync history doesn't match " +
                        $"'{masterPath}'. If this is now the right folder, empty the job's master folder and the next run re-syncs it from the devices.";
                    _logger.LogWarning("Job '{Job}' skipped - {Reason}", job.Name, reason);
                    events.JobSkipped(job.Name, reason);
                    return await FinishAsync(JobRunOutcome.SkippedProjectFolderUnavailable, reason, RunErrorCode.ProjectFolderUnavailable);
                }
                if (!eff.DryRun && recordedRoot != currentRoot)
                    await manifests.SetRootAsync(job.Name, currentRoot, ct);

                var pullStopwatch = Stopwatch.StartNew();
                var pullStats = await RunPullPhaseAsync(job, jobIndex, masterPath, serials, exclude, resumeFrom, eff, settings.MaxConcurrentPerDevice, steps, rollback, pendingManifests, unpulled, ct);
                pullDuration = pullStopwatch.Elapsed;
                // Devices merged before the checkpoint may have changed master, and that change is not visible in
                // this run's merge results, so the push must still happen.
                anyChange = pullStats.AnyChange || resumeFrom is { Phase: SyncPhase.Pull, CompletedDevices.Count: > 0 };
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
                foreach (var (deviceName, manifest) in pendingManifests)
                    await manifests.SaveAsync(job.Name, deviceName, manifest, ct);
                _logger.LogInformation("Job '{Job}' completed - no changes", job.Name);
                events.JobCompleted(job.Name, pushed: false);
                await checkpoints.ClearAsync(job.Name, ct);
                return await FinishAsync(JobRunOutcome.CompletedNoChanges);
            }

            ProjectFolders.EnsureStillAvailable(eff, masterPath);
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
            var pushStats = await RunPushPhaseAsync(job, jobIndex, masterPath, serials, exclude, resumeFrom, eff, settings.MaxConcurrentPerDevice, steps, pendingManifests, unpulled, ct);
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
            return await FinishAsync(JobRunOutcome.Failed, ex.Message, Classify(ex));
        }

        async Task<JobRunResult> FinishAsync(JobRunOutcome outcome, string? errorMessage = null, RunErrorCode? errorCode = null)
        {
            var record = new JobRunRecord(
                id, job.Name, startedAt, DateTimeOffset.UtcNow, outcome, errorMessage,
                totalFilesCopied, totalFilesDeleted, totalErrors, totalBytesCopied, pullDuration, pushDuration, errorCode);
            var finalSteps = steps.Complete(outcome);
            rollback?.Finish();
            try
            {
                await runHistory.SaveRunAsync(record, runLog.BuildText(), eff.MaxRunHistoryEntries, finalSteps, steps.Files, eff.FileDetailRunsKept, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save run history for '{Job}'", job.Name);
            }
            finally
            {
                steps.End();
            }
            return new JobRunResult(outcome, errorMessage, errorCode);
        }
    }

    private static RunErrorCode Classify(Exception ex) => ex switch
    {
        PushSafetyException => RunErrorCode.PushSafetyBlocked,
        ProjectFolderUnavailableException => RunErrorCode.ProjectFolderUnavailable,
        DeviceConnectException => RunErrorCode.DeviceUnreachable,
        ArgumentException => RunErrorCode.ValidationFailed,
        _ => RunErrorCode.InternalError,
    };

    private async Task<PhaseStats> RunPullPhaseAsync(
        SyncJobConfig job, int jobIndex, string masterPath, Dictionary<string, string> serials,
        ExcludeMatcher exclude, SyncCheckpoint? resumeFrom, EffectiveJobSettings eff, int maxConcurrentPerDevice,
        RunStepRecorder steps, RollbackSession? rollback, Dictionary<string, SyncManifest> pendingManifests,
        Dictionary<string, List<string>> unpulled, CancellationToken ct)
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
            {
                steps.Waiting(binding.DeviceName, SyncPhase.Pull);
                events.PhaseChanged(job.Name, SyncPhase.WaitingForDevice, binding.DeviceName);
            }
            var policy = eff.ToTransferPolicy();
            var incremental = false;
            SyncManifest? baseline = null;
            if (eff.IncrementalPull && !eff.DryRun && resumeFrom is null)
            {
                baseline = await manifests.TryGetAsync(job.Name, binding.DeviceName, ct);
                if (IncrementalPullPlanner.IsEligible(baseline, eff.FullPullIntervalDays, DateTimeOffset.UtcNow))
                {
                    IReadOnlyDictionary<string, FileEntry> masterFiles;
                    // Another device's merge may be writing temp files into master; scanning meanwhile can throw.
                    await bookkeepingGate.WaitAsync(ct);
                    try
                    {
                        ProjectFolders.EnsureStillAvailable(eff, masterPath);
                        masterFiles = LocalFileTreeScanner.Scan(masterPath, new ExcludeMatcher([]))
                            .Where(e => !e.IsDirectory)
                            .ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
                    }
                    finally
                    {
                        bookkeepingGate.Release();
                    }
                    policy = policy with { SkipPull = IncrementalPullPlanner.BuildSkipPredicate(baseline!, masterFiles) };
                    incremental = true;
                }
            }

            TransferResult pullResult;
            await using (await _deviceAccessGate.AcquireAsync(binding.DeviceName, maxConcurrentPerDevice, ct))
            {
                steps.Start(binding.DeviceName, SyncPhase.Pull);
                events.PhaseChanged(job.Name, SyncPhase.Pull, binding.DeviceName);
                _logger.LogInformation("Job '{Job}' pulling from '{Device}' ({Mode})", job.Name, binding.DeviceName, incremental ? "incremental" : "full");
                pullResult = await transfer.PullMirrorAsync(serial, binding.RemotePath, stagingPath, exclude, policy, ct);
            }
            var unchangedOnDevice = pullResult.SkippedUnchanged ?? [];
            _logger.LogInformation(
                "Job '{Job}' pulled from '{Device}': {Copied} copied, {Deleted} deleted, {Skipped} unchanged skipped, {Errors} error(s)",
                job.Name, binding.DeviceName, pullResult.FilesCopied, pullResult.FilesDeleted, unchangedOnDevice.Count, pullResult.Errors.Count);

            steps.Finish(binding.DeviceName, SyncPhase.Pull, pullResult.FilesCopied, pullResult.FilesDeleted, pullResult.BytesCopied, pullResult.Errors.Count);
            steps.AddFiles(TransferFiles(binding.DeviceName, SyncPhase.Pull, pullResult));

            if (pullResult.HasUnattributedErrors)
                throw new InvalidOperationException(
                    $"Pull from '{binding.DeviceName}' failed ({pullResult.Errors[0]}) - merge skipped so files that were not pulled are not treated as deletions.");
            var failedPulls = pullResult.FailedPaths ?? [];
            if (failedPulls.Count > 0)
                _logger.LogWarning(
                    "Job '{Job}' could not pull {Count} file(s) from '{Device}' - leaving them untouched on both sides this run: {Paths}",
                    job.Name, failedPulls.Count, binding.DeviceName, string.Join(", ", failedPulls));

            var fileCount = Directory.Exists(stagingPath)
                ? Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories).Count()
                : 0;
            fileCount += unchangedOnDevice.Count;

            var mergeOptions = new MergeOptions(
                BackupConflictLosers: eff.BackupConflictLosers,
                ConflictBackupDir: GetConflictBackupDir(projectRoot, binding.DeviceName),
                DryRun: eff.DryRun,
                HoldConflicts: eff.ConflictPolicy == ConflictPolicy.Ask && pendingConflicts is not null,
                SkipPaths: failedPulls,
                UnchangedOnDevice: unchangedOnDevice);

            await bookkeepingGate.WaitAsync(ct);
            try
            {
                ProjectFolders.EnsureStillAvailable(eff, masterPath);
                // Bootstrapping scans master, so it must not overlap another device's merge writing into it.
                var manifest = await manifests.GetOrBootstrapAsync(job.Name, binding.DeviceName, stagingPath, masterPath, ct);
                if (failedPulls.Count > 0)
                    unpulled[binding.DeviceName] = failedPulls.ToList();
                else
                    unpulled.Remove(binding.DeviceName);
                filesCopied += pullResult.FilesCopied;
                filesDeleted += pullResult.FilesDeleted;
                errors += pullResult.Errors.Count;
                bytesCopied += pullResult.BytesCopied;

                await pushSafety.RecordDeviceSnapshotAsync(job.Name, binding.DeviceName, fileCount, ct);

                if (rollback is not null)
                {
                    if (await WillMergeChangeAsync(job.Name, binding.DeviceName, stagingPath, masterPath, manifest, mergeOptions, ct))
                    {
                        steps.Start(binding.DeviceName, SyncPhase.Snapshot);
                        events.PhaseChanged(job.Name, SyncPhase.Snapshot, binding.DeviceName);
                        _logger.LogInformation("Job '{Job}' capturing rollback point for '{Device}'", job.Name, binding.DeviceName);
                        rollback.CaptureBeforeMerge(binding.DeviceName, stagingPath);
                        steps.Finish(binding.DeviceName, SyncPhase.Snapshot);
                    }
                    else
                    {
                        rollback.Defer(binding.DeviceName, stagingPath);
                    }
                }

                steps.Start(binding.DeviceName, SyncPhase.Merge);
                events.PhaseChanged(job.Name, SyncPhase.Merge, binding.DeviceName);
                _logger.LogInformation("Job '{Job}' merge with '{Device}' started", job.Name, binding.DeviceName);
                var mergeResult = await merge.MergeAsync(stagingPath, masterPath, manifest, mergeOptions, ct);
                if (mergeResult.AnyChange)
                    rollback?.MarkChanged(binding.DeviceName);
                // The merged manifest describes the device as it will be after the push, so it is only committed
                // once that push succeeds. Saving it now would let a failed push make the device's stale copy look
                // like a device-side edit next run, which then overwrites the newer master.
                if (!eff.DryRun)
                {
                    if (!incremental && failedPulls.Count == 0)
                        mergeResult.UpdatedManifest.LastFullPullAtUtc = DateTimeOffset.UtcNow;
                    else
                        mergeResult.UpdatedManifest.LastFullPullAtUtc = baseline?.LastFullPullAtUtc ?? manifest.LastFullPullAtUtc;
                    pendingManifests[binding.DeviceName] = mergeResult.UpdatedManifest;
                }
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

                if (mergeOptions.HoldConflicts && !eff.DryRun && pendingConflicts is not null)
                {
                    var newlyPending = await pendingConflicts.ReplaceDeviceAsync(job.Name, binding.DeviceName, mergeResult.Held, stagingPath, ct);
                    if (mergeResult.Held.Count > 0)
                    {
                        _logger.LogWarning(
                            "Job '{Job}' merge with '{Device}' is holding {Count} conflict(s) for a decision: {Paths}",
                            job.Name, binding.DeviceName, mergeResult.Held.Count,
                            string.Join(", ", mergeResult.Held.Select(c => c.RelativePath)));
                    }
                    events.ConflictsPending(job.Name, binding.DeviceName, newlyPending, (await pendingConflicts.ListAsync(job.Name, ct)).Count);
                }

                steps.Finish(
                    binding.DeviceName, SyncPhase.Merge, mergeResult.Created + mergeResult.Updated, mergeResult.Deleted,
                    conflicts: mergeResult.Conflicts.Count + mergeResult.Held.Count);
                steps.AddFiles(MergeFiles(binding.DeviceName, mergeResult));

                PruneConflictBackups(mergeOptions.ConflictBackupDir!, eff.ConflictRetentionDays);

                if (Directory.Exists(stagingPath))
                    DirectoryUtil.DeleteRecursive(stagingPath);

                completed.Add(binding.DeviceName);
                if (!eff.DryRun)
                    await checkpoints.SaveAsync(job.Name, new SyncCheckpoint(1, DateTimeOffset.UtcNow, jobIndex, job.Name, SyncPhase.Pull, completed.ToList(), serials, Snapshot(unpulled)), ct);
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
        RunStepRecorder steps, Dictionary<string, SyncManifest> pendingManifests,
        Dictionary<string, List<string>> unpulled, CancellationToken ct)
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
            IExcludeMatcher pushExclude = exclude;
            var leaveAlone = new List<string>();
            if (pendingConflicts is not null)
            {
                leaveAlone.AddRange((await pendingConflicts.ListAsync(job.Name, ct))
                    .Where(c => string.Equals(c.DeviceName, binding.DeviceName, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.RelativePath));
            }
            if (unpulled.TryGetValue(binding.DeviceName, out var notPulled))
                leaveAlone.AddRange(notPulled);
            if (leaveAlone.Count > 0)
                pushExclude = new PathSetExcludeMatcher(exclude, leaveAlone);

            if (_deviceAccessGate.IsBusy(binding.DeviceName))
            {
                steps.Waiting(binding.DeviceName, SyncPhase.Push);
                events.PhaseChanged(job.Name, SyncPhase.WaitingForDevice, binding.DeviceName);
            }
            TransferResult pushResult;
            await using (await _deviceAccessGate.AcquireAsync(binding.DeviceName, maxConcurrentPerDevice, ct))
            {
                steps.Start(binding.DeviceName, SyncPhase.Push);
                events.PhaseChanged(job.Name, SyncPhase.Push, binding.DeviceName);
                _logger.LogInformation("Job '{Job}' pushing to '{Device}'", job.Name, binding.DeviceName);
                pushResult = await transfer.PushMirrorAsync(serial, masterPath, binding.RemotePath, pushExclude, eff.ToTransferPolicy(), ct);
            }
            _logger.LogInformation(
                "Job '{Job}' pushed to '{Device}': {Copied} copied, {Deleted} deleted, {Errors} error(s)",
                job.Name, binding.DeviceName, pushResult.FilesCopied, pushResult.FilesDeleted, pushResult.Errors.Count);

            steps.Finish(binding.DeviceName, SyncPhase.Push, pushResult.FilesCopied, pushResult.FilesDeleted, pushResult.BytesCopied, pushResult.Errors.Count);
            steps.AddFiles(TransferFiles(binding.DeviceName, SyncPhase.Push, pushResult));

            await bookkeepingGate.WaitAsync(ct);
            try
            {
                filesCopied += pushResult.FilesCopied;
                filesDeleted += pushResult.FilesDeleted;
                errors += pushResult.Errors.Count;
                bytesCopied += pushResult.BytesCopied;
                copiedPaths.UnionWith(pushResult.CopiedPaths);
                deletedPaths.UnionWith(pushResult.DeletedPaths);

                if (pendingManifests.TryGetValue(binding.DeviceName, out var pendingManifest))
                {
                    if (pushResult.Errors.Count == 0)
                        await manifests.SaveAsync(job.Name, binding.DeviceName, pendingManifest, ct);
                    else
                        _logger.LogWarning(
                            "Job '{Job}' push to '{Device}' had {Count} error(s) - keeping the previous manifest so the next run retries instead of treating the device's old files as edits",
                            job.Name, binding.DeviceName, pushResult.Errors.Count);
                }

                completed.Add(binding.DeviceName);
                await checkpoints.SaveAsync(job.Name, new SyncCheckpoint(1, DateTimeOffset.UtcNow, jobIndex, job.Name, SyncPhase.Push, completed.ToList(), serials, Snapshot(unpulled)), ct);
            }
            finally
            {
                bookkeepingGate.Release();
            }
        }

        await Task.WhenAll(pending.Select(ProcessDeviceAsync));

        return new PhaseStats(AnyChange: false, filesCopied, filesDeleted, errors, bytesCopied, copiedPaths.ToList(), deletedPaths.ToList());
    }

    private static IEnumerable<RunFileChange> TransferFiles(string device, SyncPhase phase, TransferResult result) =>
        result.CopiedPaths.Select(p => new RunFileChange(device, phase, p, RunFileAction.Copied))
            .Concat(result.DeletedPaths.Select(p => new RunFileChange(device, phase, p, RunFileAction.Deleted)))
            .Concat(result.Errors.Select(e => new RunFileChange(device, phase, "", RunFileAction.Error, Detail: e)));

    private static IEnumerable<RunFileChange> MergeFiles(string device, MergeResult result) =>
        result.Changes.Select(c => new RunFileChange(
                device, SyncPhase.Merge, c.RelativePath,
                c.Kind switch { MergeChangeKind.Created => RunFileAction.Created, MergeChangeKind.Updated => RunFileAction.Updated, _ => RunFileAction.Deleted },
                c.Size,
                c.Kind == MergeChangeKind.Deleted
                    ? (c.ToDevice ? "removed from device" : "removed from master")
                    : (c.ToDevice ? "master → device" : "device → master")))
            .Concat(result.Conflicts.Select(c => new RunFileChange(
                device, SyncPhase.Merge, c.RelativePath, RunFileAction.Conflict,
                Detail: (c.WinningSide == "staging" ? "device version kept" : "master version kept") + (c.BackupPath is null ? "" : ", other side backed up"))))
            .Concat(result.Held.Select(h => new RunFileChange(
                device, SyncPhase.Merge, h.RelativePath, RunFileAction.Held, h.StagingSize ?? h.MasterSize, "waiting for your decision")));

    private static Dictionary<string, List<string>>? Snapshot(Dictionary<string, List<string>> unpulled) =>
        unpulled.Count == 0 ? null : unpulled.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);

    private readonly record struct PhaseStats(
        bool AnyChange, int FilesCopied, int FilesDeleted, int Errors, long BytesCopied,
        IReadOnlyList<string> CopiedPaths, IReadOnlyList<string> DeletedPaths);

    private static string GetStagingPath(string projectRoot, string deviceName)
    {
        var path = Path.Combine(SyncLockManager.GetStagingRoot(projectRoot), ConfigNames.Segment(deviceName));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetConflictBackupDir(string projectRoot, string deviceName) =>
        Path.Combine(projectRoot, ".sync_conflicts", ConfigNames.Segment(deviceName));

    private async Task<bool> WillMergeChangeAsync(
        string jobName, string deviceName, string stagingPath, string masterPath, SyncManifest manifest, MergeOptions options, CancellationToken ct)
    {
        try
        {
            return (await merge.MergeAsync(stagingPath, masterPath, manifest, options with { DryRun = true }, ct)).AnyChange;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Job '{Job}' could not pre-check the merge with '{Device}' - capturing a rollback point to be safe", jobName, deviceName);
            return true;
        }
    }

    /// <summary>Lazily creates one rollback point per run, the first time a device is about to merge a change (so
    /// master is still untouched), and drops it again at the end if no merge changed anything - an idle schedule
    /// tick must not copy anything or push real rollback points out of the keep-last-N window. Devices that merged
    /// nothing before the point exists are deferred and captured with it, because the push still gives them the new
    /// master. A failure to copy never fails the sync.</summary>
    private sealed class RollbackSession(RollbackPointStore store, string projectRoot, string masterPath, int keep, ILogger logger)
    {
        private readonly List<(string Device, string Staging)> _deferred = [];
        private string? _point;
        private bool _failed;
        private bool _changed;

        public void Defer(string deviceName, string stagingPath)
        {
            if (!_failed && _point is null)
                _deferred.Add((deviceName, stagingPath));
        }

        public void CaptureBeforeMerge(string deviceName, string stagingPath)
        {
            if (_failed)
                return;

            try
            {
                if (_point is null)
                {
                    _point = store.Create(projectRoot);
                    store.CaptureMaster(_point, masterPath);
                    foreach (var (device, staging) in _deferred)
                        store.CaptureDevice(_point, device, staging);
                    _deferred.Clear();
                }
                store.CaptureDevice(_point, deviceName, stagingPath);
            }
            catch (Exception ex)
            {
                _failed = true;
                logger.LogWarning(ex, "Could not capture a rollback point for '{Path}' - continuing the sync without one", projectRoot);
                Discard();
            }
        }

        public void MarkChanged(string deviceName)
        {
            if (_point is null && !_failed)
                logger.LogWarning("Merge with '{Device}' changed files that the pre-check did not predict - no rollback point exists for this run", deviceName);
            _changed = true;
        }

        public void Finish()
        {
            if (!_changed)
                Discard();

            try
            {
                store.Prune(projectRoot, keep);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not prune old rollback points under '{Path}'", projectRoot);
            }
        }

        private void Discard()
        {
            if (_point is null)
                return;

            try
            {
                store.Discard(_point);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove rollback point '{Point}'", _point);
            }
            _point = null;
        }
    }

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
