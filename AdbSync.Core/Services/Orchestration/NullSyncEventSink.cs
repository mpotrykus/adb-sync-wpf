using AdbSync.Core.Models.Orchestration;

namespace AdbSync.Core.Services.Orchestration;

public sealed class NullSyncEventSink : ISyncEventSink
{
    public static readonly NullSyncEventSink Instance = new();

    public void PhaseChanged(string jobName, SyncPhase phase, string? deviceName = null) { }
    public void JobSkipped(string jobName, string reason) { }
    public void JobCompleted(string jobName, bool pushed) { }
    public void JobFailed(string jobName, Exception exception) { }
    public void JobCancelled(string jobName) { }
    public void MergeConflictsDetected(string jobName, string deviceName, int conflictCount) { }
    public void WatchStarted(string jobName, string deviceName, bool liveWatch) { }
    public void WatchDegraded(string jobName, string deviceName, string reason) { }
    public void WatchStopped(string jobName, string deviceName) { }
    public void ChangeDetected(string jobName, string deviceName) { }
}
