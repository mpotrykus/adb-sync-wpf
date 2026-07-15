using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;

namespace AdbSync.Core.Tests.Orchestration.Fakes;

public sealed class RecordingSyncEventSink : ISyncEventSink
{
    public bool Failed { get; private set; }
    public Exception? LastException { get; private set; }
    public bool Cancelled { get; private set; }
    public int TotalConflictsReported { get; private set; }
    public List<(string JobName, string DeviceName, bool LiveWatch)> WatchStartedCalls { get; } = [];
    public List<(string JobName, string DeviceName, string Reason)> WatchDegradedCalls { get; } = [];
    public List<(string JobName, string DeviceName)> WatchStoppedCalls { get; } = [];
    public List<(string JobName, string DeviceName)> ChangeDetectedCalls { get; } = [];

    public void PhaseChanged(string jobName, SyncPhase phase, string? deviceName = null) { }
    public void JobSkipped(string jobName, string reason) { }
    public void JobCompleted(string jobName, bool pushed) { }

    public void JobFailed(string jobName, Exception exception)
    {
        Failed = true;
        LastException = exception;
    }

    public void JobCancelled(string jobName) => Cancelled = true;

    public void MergeConflictsDetected(string jobName, string deviceName, int conflictCount) =>
        TotalConflictsReported += conflictCount;

    public void WatchStarted(string jobName, string deviceName, bool liveWatch) =>
        WatchStartedCalls.Add((jobName, deviceName, liveWatch));

    public void WatchDegraded(string jobName, string deviceName, string reason) =>
        WatchDegradedCalls.Add((jobName, deviceName, reason));

    public void WatchStopped(string jobName, string deviceName) =>
        WatchStoppedCalls.Add((jobName, deviceName));

    public void ChangeDetected(string jobName, string deviceName) =>
        ChangeDetectedCalls.Add((jobName, deviceName));
}
