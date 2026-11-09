using AdbSync.Core.Models.Orchestration.RunHistory;

namespace AdbSync.Core.Services.Logging;

/// <summary>Latest per-device/per-phase step snapshot for whichever run is in progress per job, so a UI can draw
/// the run's execution graph live instead of waiting for it to be persisted to <c>IRunHistoryStore</c>.</summary>
public interface ILiveRunStepSink
{
    /// <summary>Raised (on an arbitrary thread) after a job's steps change or its run ends.</summary>
    event Action<string>? Changed;

    void Publish(string jobName, IReadOnlyList<RunStepRecord> steps);
    void End(string jobName);
    bool TryGet(string jobName, out IReadOnlyList<RunStepRecord> steps);
}
