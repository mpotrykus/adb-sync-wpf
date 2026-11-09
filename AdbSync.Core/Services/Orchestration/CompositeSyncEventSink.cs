using AdbSync.Core.Models.Orchestration;

namespace AdbSync.Core.Services.Orchestration;

public sealed class CompositeSyncEventSink(IReadOnlyList<ISyncEventSink> sinks) : ISyncEventSink
{
    public void PhaseChanged(string jobName, SyncPhase phase, string? deviceName = null)
    {
        foreach (var sink in sinks) sink.PhaseChanged(jobName, phase, deviceName);
    }

    public void JobQueued(string jobName, string reason)
    {
        foreach (var sink in sinks) sink.JobQueued(jobName, reason);
    }

    public void JobSkipped(string jobName, string reason)
    {
        foreach (var sink in sinks) sink.JobSkipped(jobName, reason);
    }

    public void JobCompleted(string jobName, bool pushed)
    {
        foreach (var sink in sinks) sink.JobCompleted(jobName, pushed);
    }

    public void JobFailed(string jobName, Exception exception)
    {
        foreach (var sink in sinks) sink.JobFailed(jobName, exception);
    }

    public void JobCancelled(string jobName)
    {
        foreach (var sink in sinks) sink.JobCancelled(jobName);
    }

    public void MergeConflictsDetected(string jobName, string deviceName, int conflictCount)
    {
        foreach (var sink in sinks) sink.MergeConflictsDetected(jobName, deviceName, conflictCount);
    }

    public void WatchStarted(string jobName, string deviceName, bool liveWatch)
    {
        foreach (var sink in sinks) sink.WatchStarted(jobName, deviceName, liveWatch);
    }

    public void WatchDegraded(string jobName, string deviceName, string reason)
    {
        foreach (var sink in sinks) sink.WatchDegraded(jobName, deviceName, reason);
    }

    public void WatchStopped(string jobName, string deviceName)
    {
        foreach (var sink in sinks) sink.WatchStopped(jobName, deviceName);
    }

    public void ChangeDetected(string jobName, string deviceName)
    {
        foreach (var sink in sinks) sink.ChangeDetected(jobName, deviceName);
    }

    public void RunStarted(string jobName, bool dryRun)
    {
        foreach (var sink in sinks) sink.RunStarted(jobName, dryRun);
    }

    public void JobBlockedByApp(string jobName, string deviceName)
    {
        foreach (var sink in sinks) sink.JobBlockedByApp(jobName, deviceName);
    }
}
