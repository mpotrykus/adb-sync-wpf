using AdbSync.Core.Orchestration;

namespace AdbSync.Core.Tests.Orchestration.Fakes;

public sealed class RecordingSyncEventSink : ISyncEventSink
{
    public bool Failed { get; private set; }
    public Exception? LastException { get; private set; }
    public int TotalConflictsReported { get; private set; }

    public void PhaseChanged(string jobName, SyncPhase phase, string? deviceName = null) { }
    public void JobSkipped(string jobName, string reason) { }
    public void JobCompleted(string jobName, bool pushed) { }

    public void JobFailed(string jobName, Exception exception)
    {
        Failed = true;
        LastException = exception;
    }

    public void MergeConflictsDetected(string jobName, string deviceName, int conflictCount) =>
        TotalConflictsReported += conflictCount;
}
