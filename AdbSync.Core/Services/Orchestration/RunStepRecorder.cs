using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using AdbSync.Core.Services.Logging;

namespace AdbSync.Core.Services.Orchestration;

/// <summary>Collects one run's per-device/per-phase steps and mirrors every change to the live sink.</summary>
internal sealed class RunStepRecorder(string jobName, ILiveRunStepSink? liveSink)
{
    public static readonly SyncPhase[] GraphPhases = [SyncPhase.PreConnect, SyncPhase.Pull, SyncPhase.Snapshot, SyncPhase.Merge, SyncPhase.Push];

    private readonly List<RunStepRecord> _steps = [];
    private readonly List<RunFileChange> _files = [];
    private readonly object _gate = new();

    public void Plan(IEnumerable<string> deviceNames)
    {
        lock (_gate)
        {
            foreach (var device in deviceNames)
                foreach (var phase in GraphPhases)
                    _steps.Add(new RunStepRecord(device, phase, RunStepStatus.Pending, null, null));
            Publish();
        }
    }

    public void Waiting(string device, SyncPhase phase) =>
        Update(device, phase, s => s with { Status = RunStepStatus.Waiting });

    public void Start(string device, SyncPhase phase) =>
        Update(device, phase, s => s with { Status = RunStepStatus.Running, StartedAt = s.StartedAt ?? DateTimeOffset.Now });

    public void Finish(
        string device, SyncPhase phase, int filesCopied = 0, int filesDeleted = 0, long bytesCopied = 0, int errors = 0, int conflicts = 0) =>
        Update(device, phase, s => s with
        {
            Status = errors > 0 ? RunStepStatus.Failed : RunStepStatus.Succeeded,
            StartedAt = s.StartedAt ?? DateTimeOffset.Now,
            EndedAt = DateTimeOffset.Now,
            FilesCopied = filesCopied,
            FilesDeleted = filesDeleted,
            BytesCopied = bytesCopied,
            Errors = errors,
            Conflicts = conflicts,
        });

    public void AddFiles(IEnumerable<RunFileChange> files)
    {
        lock (_gate)
            _files.AddRange(files);
    }

    public IReadOnlyList<RunFileChange> Files
    {
        get
        {
            lock (_gate)
                return _files.ToList();
        }
    }

    public IReadOnlyList<RunStepRecord> Complete(JobRunOutcome outcome)
    {
        lock (_gate)
        {
            var interrupted = outcome == JobRunOutcome.Cancelled ? RunStepStatus.Cancelled : RunStepStatus.Failed;
            var now = DateTimeOffset.Now;
            for (var i = 0; i < _steps.Count; i++)
            {
                var step = _steps[i];
                if (step.Status is RunStepStatus.Running or RunStepStatus.Waiting)
                    _steps[i] = step with { Status = interrupted, EndedAt = now };
                else if (step.Status == RunStepStatus.Pending)
                    _steps[i] = step with { Status = RunStepStatus.Skipped };
            }

            Publish();
            return _steps.ToList();
        }
    }

    public void End() => liveSink?.End(jobName);

    private void Update(string device, SyncPhase phase, Func<RunStepRecord, RunStepRecord> change)
    {
        lock (_gate)
        {
            var index = _steps.FindIndex(s => s.Phase == phase && string.Equals(s.DeviceName, device, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;
            _steps[index] = change(_steps[index]);
            Publish();
        }
    }

    private void Publish() => liveSink?.Publish(jobName, _steps.ToList());
}
