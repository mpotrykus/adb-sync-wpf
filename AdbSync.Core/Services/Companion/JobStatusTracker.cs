using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using AdbSync.Core.Services.Orchestration;

namespace AdbSync.Core.Services.Companion;

public sealed record DevicePhase(string Device, string Phase);

public sealed record JobStatus(
    string Name,
    bool Running,
    string? Phase,
    IReadOnlyList<DevicePhase> Devices,
    string? LastResult,
    bool LastFailed,
    DateTimeOffset? LastResultAt,
    IReadOnlyList<string>? PendingChangeDevices = null,
    string? BlockedDevice = null);

/// <summary>Thread-safe per-job snapshot of what's happening right now, fed by run events, for remote viewers
/// (the companion app's HTTP feed). Mirrors what the dashboard shows without being tied to the UI thread.</summary>
public sealed class JobStatusTracker : ISyncEventSink
{
    private readonly object _gate = new();
    private readonly Dictionary<string, State> _jobs = new(StringComparer.Ordinal);

    public event Action<JobStatus>? Changed;

    public IReadOnlyList<JobStatus> Snapshot()
    {
        lock (_gate)
            return _jobs.Values.Select(s => s.ToStatus()).ToList();
    }

    public JobStatus Get(string jobName)
    {
        lock (_gate)
            return _jobs.TryGetValue(jobName, out var s) ? s.ToStatus() : new State(jobName).ToStatus();
    }

    /// <summary>Re-announces a job's current status after something outside the run events changed (enabled flag, checkpoint).</summary>
    public void Republish(string jobName) => Update(jobName, _ => true);

    /// <summary>Fills in the last result from run history for a job this session hasn't seen finish yet.</summary>
    public void SeedFromHistory(JobRunRecord record)
    {
        Update(record.JobName, s =>
        {
            if (s.LastResult is not null)
                return false;
            (s.LastResult, s.LastFailed) = Describe(record);
            s.LastResultAt = record.CompletedAt;
            return true;
        });
    }

    public void PhaseChanged(string jobName, SyncPhase phase, string? deviceName = null) =>
        Update(jobName, s =>
        {
            s.Running = true;
            if (deviceName is null)
            {
                s.Devices.Clear();
                s.Phase = phase.ToString();
            }
            else
            {
                s.Devices[deviceName] = phase.ToString();
            }
            if (phase == SyncPhase.PreConnect)
                s.Conflicts = 0;
            return true;
        });

    public void JobQueued(string jobName, string reason) =>
        Update(jobName, s =>
        {
            s.Running = true;
            s.Devices.Clear();
            s.Phase = "Queued";
            return true;
        });

    public void JobSkipped(string jobName, string reason) =>
        Update(jobName, s =>
        {
            if (reason == SyncJobRunner.AlreadyRunningReason && s.Running)
                return false;
            Finish(s, $"Skipped: {reason}", failed: false);
            return true;
        });

    public void JobCompleted(string jobName, bool pushed) =>
        Update(jobName, s =>
        {
            var outcome = pushed ? "Success" : "No changes";
            if (!s.DryRun)
            {
                s.PendingChanges.Clear();
                s.BlockedDevice = null;
            }
            Finish(s, s.Conflicts > 0 ? $"{outcome} ({s.Conflicts} conflict(s) resolved)" : outcome, failed: false);
            return true;
        });

    public void JobFailed(string jobName, Exception exception) =>
        Update(jobName, s =>
        {
            Finish(s, $"Error: {exception.Message}", failed: true);
            return true;
        });

    public void JobCancelled(string jobName) =>
        Update(jobName, s =>
        {
            if (!s.Running && s.LastResult == "Stopped")
                return false;
            Finish(s, "Stopped", failed: false);
            return true;
        });

    public void MergeConflictsDetected(string jobName, string deviceName, int conflictCount) =>
        Update(jobName, s =>
        {
            s.Conflicts += conflictCount;
            return false;
        });

    public void RunStarted(string jobName, bool dryRun) =>
        Update(jobName, s =>
        {
            s.DryRun = dryRun;
            if (dryRun || s.BlockedDevice is null)
                return false;
            s.BlockedDevice = null;
            return true;
        });

    public void JobBlockedByApp(string jobName, string deviceName) =>
        Update(jobName, s =>
        {
            if (s.BlockedDevice == deviceName)
                return false;
            s.BlockedDevice = deviceName;
            return true;
        });

    public void ChangeDetected(string jobName, string deviceName) =>
        Update(jobName, s => s.PendingChanges.Add(deviceName));

    public void WatchStarted(string jobName, string deviceName, bool liveWatch) { }
    public void WatchDegraded(string jobName, string deviceName, string reason) { }
    public void WatchStopped(string jobName, string deviceName) { }

    private static void Finish(State s, string result, bool failed)
    {
        s.Running = false;
        s.Phase = null;
        s.Devices.Clear();
        s.LastResult = result;
        s.LastFailed = failed;
        s.LastResultAt = DateTimeOffset.Now;
    }

    private static (string Result, bool Failed) Describe(JobRunRecord r) => r.Outcome switch
    {
        JobRunOutcome.Completed => ("Success", false),
        JobRunOutcome.CompletedNoChanges => ("No changes", false),
        JobRunOutcome.DryRunCompleted => ("Dry run completed", false),
        JobRunOutcome.Skipped => ("Skipped", false),
        JobRunOutcome.SkippedAppRunning => ("Skipped: app running", false),
        JobRunOutcome.Cancelled => ("Stopped", false),
        JobRunOutcome.Failed => ($"Error: {r.ErrorMessage}", true),
        _ => (r.Outcome.ToString(), false),
    };

    private void Update(string jobName, Func<State, bool> apply)
    {
        JobStatus status;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobName, out var s))
                _jobs[jobName] = s = new State(jobName);
            if (!apply(s))
                return;
            status = s.ToStatus();
        }
        Changed?.Invoke(status);
    }

    private sealed class State(string name)
    {
        public bool Running;
        public string? Phase;
        public readonly Dictionary<string, string> Devices = new(StringComparer.Ordinal);
        public string? LastResult;
        public bool LastFailed;
        public DateTimeOffset? LastResultAt;
        public int Conflicts;
        public bool DryRun;
        public string? BlockedDevice;
        public readonly HashSet<string> PendingChanges = new(StringComparer.Ordinal);

        public JobStatus ToStatus() => new(
            name, Running, Phase,
            Devices.Select(kv => new DevicePhase(kv.Key, kv.Value)).ToList(),
            LastResult, LastFailed, LastResultAt, PendingChanges.Order(StringComparer.Ordinal).ToList(), BlockedDevice);
    }
}
