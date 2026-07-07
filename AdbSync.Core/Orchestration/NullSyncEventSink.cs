namespace AdbSync.Core.Orchestration;

public sealed class NullSyncEventSink : ISyncEventSink
{
    public static readonly NullSyncEventSink Instance = new();

    public void PhaseChanged(string jobName, SyncPhase phase, string? deviceName = null) { }
    public void JobSkipped(string jobName, string reason) { }
    public void JobCompleted(string jobName, bool pushed) { }
    public void JobFailed(string jobName, Exception exception) { }
    public void MergeConflictsDetected(string jobName, string deviceName, int conflictCount) { }
}
