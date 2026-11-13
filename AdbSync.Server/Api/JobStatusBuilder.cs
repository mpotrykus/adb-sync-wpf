using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Companion;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Orchestration.RunHistory;
using AdbSync.Core.Services.Scheduling;

namespace AdbSync.Server.Api;

/// <summary>Assembles the contract's JobStatus from the engine's live tracker, the config and the stored history.</summary>
public sealed class JobStatusBuilder(
    JobStatusTracker tracker, IRunHistoryStore history, ICheckpointManager checkpoints, IPendingConflictStore conflicts, RunRegistry runs, WatchStateTracker watch, Engine.Services.AppGateService gate)
{
    public async Task<JobStatusDto> BuildAsync(SyncJobConfig job, AppConfig config, CancellationToken ct = default)
    {
        var live = tracker.Get(job.Name);
        var deviceIds = config.Devices.ToDictionary(d => d.Name, d => d.Id, StringComparer.OrdinalIgnoreCase);
        var lastRecord = (await history.ListRunsAsync(job.Name, ct)).FirstOrDefault();
        var checkpoint = await checkpoints.LoadAsync(job.Name, ct);
        var pending = (await conflicts.ListAsync(job.Name, ct)).Count;
        var eff = job.Resolve(config.Settings);
        var unavailable = ProjectFolders.Unavailable(eff, job.Name);
        var stale = job.AppPackage is null ? [] : await gate.AssessAsync(job.Name);

        return new JobStatusDto(
            job.Id,
            job.Name,
            job.Enabled,
            State(live),
            runs.ActiveFor(job.Id)?.Id,
            live.Running ? JobPhase(live) : null,
            live.Devices
                .Where(d => deviceIds.ContainsKey(d.Device) && Contract.SyncPhase(d.Phase) is not null)
                .Select(d => new DevicePhaseDto(deviceIds[d.Device], Contract.SyncPhase(d.Phase)!))
                .ToList(),
            lastRecord is null ? null : new RunSummaryDto(
                lastRecord.RunId, Contract.RunStatus(lastRecord.Outcome), lastRecord.CompletedAt,
                Contract.ErrorCode(lastRecord.ErrorCode), lastRecord.ErrorMessage),
            job.Enabled ? ScheduleCalculator.NextDueUtc(job.Schedule, ScheduleClock.Now(config.Settings), config.Settings.RunMissedSchedules) : null,
            checkpoint is null ? null : new CheckpointSummaryDto(
                checkpoint.SavedAt, Contract.SyncPhase(checkpoint.Phase),
                $"{checkpoint.Phase} done on {checkpoint.CompletedDevices.Count} of {job.Devices.Count} devices"),
            pending,
            job.AppPackage,
            stale.Where(s => deviceIds.ContainsKey(s.Device)).Select(s => new StaleDeviceDto(deviceIds[s.Device], s.Reason)).ToList(),
            live.BlockedDevice is { } blocked && deviceIds.TryGetValue(blocked, out var blockedId) ? blockedId : null,
            lastRecord?.ErrorCode == RunErrorCode.PushSafetyBlocked,
            new AvailabilityDto(unavailable is null ? "ok" : "unavailable", unavailable),
            watch.For(job, config));
    }

    private static readonly string[] PhaseOrder = ["preConnect", "waitingForDevice", "pull", "snapshot", "merge", "push"];

    private static string? JobPhase(Core.Services.Companion.JobStatus live)
    {
        var jobPhase = Contract.SyncPhase(live.Phase);
        var furthest = live.Devices
            .Select(d => Contract.SyncPhase(d.Phase))
            .Where(p => p is not null && Array.IndexOf(PhaseOrder, p) >= 0)
            .MaxBy(p => Array.IndexOf(PhaseOrder, p));
        return furthest is not null && Array.IndexOf(PhaseOrder, furthest) > Array.IndexOf(PhaseOrder, jobPhase) ? furthest : jobPhase;
    }

    private static string State(Core.Services.Companion.JobStatus live) => live switch
    {
        { Running: false } => "idle",
        { Phase: "Queued" } => "queued",
        { Phase: nameof(SyncPhase.WaitingForAppClose) } => "waitingForApp",
        _ => "running",
    };
}
