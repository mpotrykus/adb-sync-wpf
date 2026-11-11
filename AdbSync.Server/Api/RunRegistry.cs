using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Orchestration.RunHistory;
using AdbSync.Engine.Services;
using AdbSync.Server.Events;
using System.Collections.Concurrent;

namespace AdbSync.Server.Api;

public sealed record RunDto(
    Guid Id, Guid JobId, string JobName, string Status, string Mode, string Trigger, Guid? RequestedBy,
    DateTimeOffset QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? ErrorCode, string? Message,
    int FilesCopied, int FilesDeleted, int ErrorCount, long BytesCopied, double? PullSeconds, double? PushSeconds);

public sealed record RunStepDto(
    Guid DeviceId, string DeviceName, string Phase, string Status, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt,
    int FilesCopied, int FilesDeleted, long BytesCopied, int Errors, int Conflicts);

public sealed record RunDetailDto(RunDto Run, IReadOnlyList<RunStepDto> Steps);

/// <summary>
/// Runs that haven't finished yet (queued or running), which run history doesn't know about, plus the events that
/// follow a run from queued to finished. Finished runs are read from run history.
/// </summary>
public sealed class RunRegistry : ISyncEventSink, IDisposable
{
    private readonly ConcurrentDictionary<Guid, RunDto> _active = new();
    private readonly ILiveRunStepSink _liveSteps;
    private readonly AppConfigService _config;
    private readonly EventBus _bus;

    private JobRunService? _runs;

    private readonly IRunHistoryStore _history;

    public RunRegistry(ILiveRunStepSink liveSteps, AppConfigService config, EventBus bus, IRunHistoryStore history)
    {
        _history = history;
        _liveSteps = liveSteps;
        _config = config;
        _bus = bus;
        _liveSteps.Changed += OnStepsChanged;
    }

    /// <summary>
    /// Separate from the constructor because JobRunService depends on the event sink this registry is part of, so
    /// taking it as a constructor dependency would be a cycle.
    /// </summary>
    public void Attach(JobRunService runs)
    {
        _runs = runs;
        runs.RunQueued += OnQueued;
        runs.RunFinished += OnFinished;
    }

    public RunDto? Get(Guid runId) => _active.GetValueOrDefault(runId);

    public RunDto? ActiveFor(Guid jobId) => _active.Values.Where(r => r.JobId == jobId).OrderBy(r => r.QueuedAt).FirstOrDefault();

    public IReadOnlyList<RunDto> Active => _active.Values.OrderBy(r => r.QueuedAt).ToList();

    private void OnQueued(RunRequest request)
    {
        var run = new RunDto(
            request.RunId, request.JobId, request.JobName, "queued", Mode(request), Trigger(request.Trigger), request.RequestedBy,
            request.QueuedAt, null, null, null, null, 0, 0, 0, 0, null, null);
        _active[request.RunId] = run;
        _bus.Publish("run.queued", run);
    }

    /// <summary>The runner's RunStarted event, which only carries the job name.</summary>
    public void RunStarted(string jobName, bool dryRun)
    {
        foreach (var run in _active.Values.Where(r => r.JobName == jobName && r.Status == "queued"))
        {
            var started = run with { Status = "running", StartedAt = DateTimeOffset.UtcNow };
            _active[run.Id] = started;
            _bus.Publish("run.started", started);
        }
    }

    private async void OnFinished(RunRequest request, JobRunResult result)
    {
        if (!_active.TryRemove(request.RunId, out var run))
            return;
        try
        {
            await _history.SetRunContextAsync(run.Id, run.Mode, run.Trigger, run.RequestedBy);
        }
        catch (Exception)
        {
        }
        _bus.Publish("run.finished", run with
        {
            Status = Contract.RunStatus(result.Outcome),
            FinishedAt = DateTimeOffset.UtcNow,
            ErrorCode = Contract.ErrorCode(result.ErrorCode),
            Message = result.ErrorMessage,
        });
    }

    private async void OnStepsChanged(string jobName)
    {
        var run = _active.Values.FirstOrDefault(r => r.JobName == jobName);
        if (run is null || !_liveSteps.TryGet(jobName, out var steps))
            return;
        try
        {
            var config = await _config.GetAsync();
            _bus.Publish("run.steps", new { runId = run.Id, jobId = run.JobId, steps = ToDtos(steps, config.Devices.ToDictionary(d => d.Name, d => d.Id, StringComparer.OrdinalIgnoreCase)) });
        }
        catch (Exception)
        {
        }
    }

    public static RunDto FromHistory(JobRunRecord record, Guid jobId) => new(
        record.RunId, jobId, record.JobName, Contract.RunStatus(record.Outcome), record.Mode ?? "normal", record.Trigger ?? "manual", record.RequestedBy,
        record.StartedAt, record.StartedAt, record.CompletedAt, Contract.ErrorCode(record.ErrorCode), record.ErrorMessage,
        record.FilesCopied, record.FilesDeleted, record.ErrorCount, record.BytesCopied,
        record.PullDuration?.TotalSeconds, record.PushDuration?.TotalSeconds);

    public static IReadOnlyList<RunStepDto> ToDtos(IReadOnlyList<RunStepRecord> steps, IReadOnlyDictionary<string, Guid> deviceIds) =>
        steps.Select(s => new RunStepDto(
            deviceIds.GetValueOrDefault(s.DeviceName), s.DeviceName, Contract.SyncPhase(s.Phase), StepStatus(s.Status),
            s.StartedAt, s.EndedAt, s.FilesCopied, s.FilesDeleted, s.BytesCopied, s.Errors, s.Conflicts)).ToList();

    private static string StepStatus(RunStepStatus status) => status switch
    {
        RunStepStatus.Pending => "pending",
        RunStepStatus.Waiting => "waiting",
        RunStepStatus.Running => "running",
        RunStepStatus.Succeeded => "succeeded",
        RunStepStatus.Failed => "failed",
        RunStepStatus.Skipped => "skipped",
        _ => "cancelled",
    };

    private static string Mode(RunRequest request) => request.ForcePush ? "forcePush" : request.DryRun ? "dryRun" : "normal";

    private static string Trigger(RunTrigger trigger) => trigger switch
    {
        RunTrigger.Schedule => "schedule",
        RunTrigger.OnChange => "onChange",
        RunTrigger.ConflictResolved => "conflictResolved",
        RunTrigger.CloseAndSync => "closeAndSync",
        RunTrigger.RunAll => "runAll",
        RunTrigger.MissedSchedule => "missedSchedule",
        _ => "manual",
    };

    public void Dispose()
    {
        if (_runs is not null)
        {
            _runs.RunQueued -= OnQueued;
            _runs.RunFinished -= OnFinished;
        }
        _liveSteps.Changed -= OnStepsChanged;
    }

    public void PhaseChanged(string jobName, SyncPhase phase, string? deviceName = null) { }
    public void JobQueued(string jobName, string reason) { }
    public void JobSkipped(string jobName, string reason) { }
    public void JobCompleted(string jobName, bool pushed) { }
    public void JobFailed(string jobName, Exception exception) { }
    public void JobCancelled(string jobName) { }
    public void MergeConflictsDetected(string jobName, string deviceName, int conflictCount) =>
        PublishConflicts(jobName, deviceName, autoResolved: conflictCount, held: 0, totalPending: null);

    public void ConflictsPending(string jobName, string deviceName, int newCount, int totalPending) =>
        PublishConflicts(jobName, deviceName, autoResolved: 0, held: newCount, totalPending: totalPending);

    /// <summary>Raised after held conflicts are stored, so listeners can announce the new ones.</summary>
    public event Action<string, string>? ConflictsHeld;

    private async void PublishConflicts(string jobName, string deviceName, int autoResolved, int held, int? totalPending)
    {
        if (_active.Values.FirstOrDefault(r => r.JobName == jobName) is not { } run)
            return;
        try
        {
            var config = await _config.GetAsync();
            var deviceId = config.Devices.FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase))?.Id;
            _bus.Publish("run.conflicts", new { runId = run.Id, jobId = run.JobId, deviceId, autoResolved, held, totalPending });
            if (held > 0)
                ConflictsHeld?.Invoke(jobName, deviceName);
        }
        catch (Exception)
        {
        }
    }
    public void WatchStarted(string jobName, string deviceName, bool liveWatch) { }
    public void WatchDegraded(string jobName, string deviceName, string reason) { }
    public void WatchStopped(string jobName, string deviceName) { }
    public void ChangeDetected(string jobName, string deviceName) { }
}
