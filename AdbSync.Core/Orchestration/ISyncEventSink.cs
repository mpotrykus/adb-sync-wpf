namespace AdbSync.Core.Orchestration;

/// <summary>Live status hook for whatever's watching a run - tray tooltip, dashboard, notifications (wired up in the WPF shell).</summary>
public interface ISyncEventSink
{
    void PhaseChanged(string jobName, SyncPhase phase, string? deviceName = null);
    void JobSkipped(string jobName, string reason);
    void JobCompleted(string jobName, bool pushed);
    void JobFailed(string jobName, Exception exception);

    /// <summary>Reported once per device after a merge that found conflicts - conflict losers are always backed up to disk, never silently discarded.</summary>
    void MergeConflictsDetected(string jobName, string deviceName, int conflictCount);
}
