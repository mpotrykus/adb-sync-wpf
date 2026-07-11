using AdbSync.Core.Models.Orchestration;

namespace AdbSync.Core.Services.Orchestration;

/// <summary>Live status hook for whatever's watching a run - tray tooltip, dashboard, notifications (wired up in the WPF shell).</summary>
public interface ISyncEventSink
{
    void PhaseChanged(string jobName, SyncPhase phase, string? deviceName = null);
    void JobSkipped(string jobName, string reason);
    void JobCompleted(string jobName, bool pushed);
    void JobFailed(string jobName, Exception exception);

    /// <summary>Reported once per device after a merge that found conflicts - conflict losers are always backed up to disk, never silently discarded.</summary>
    void MergeConflictsDetected(string jobName, string deviceName, int conflictCount);

    /// <summary>An OnChange watcher started for this device binding; <paramref name="liveWatch"/> is false if it's polling instead.</summary>
    void WatchStarted(string jobName, string deviceName, bool liveWatch);

    /// <summary>A live watch fell back to polling (device disconnected, inotifyd unavailable, path unreadable, etc).</summary>
    void WatchDegraded(string jobName, string deviceName, string reason);

    /// <summary>An OnChange watcher stopped (job disabled, schedule changed, binding removed).</summary>
    void WatchStopped(string jobName, string deviceName);

    /// <summary>A change was observed on a device; the job run it triggers (after the debounce window) is reported separately via the usual PhaseChanged/JobCompleted calls.</summary>
    void ChangeDetected(string jobName, string deviceName);
}
