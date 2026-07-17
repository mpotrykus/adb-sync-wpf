using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;

namespace AdbSync.Cli;

public sealed class ConsoleSyncEventSink : ISyncEventSink
{
    public void PhaseChanged(string jobName, SyncPhase phase, string? deviceName = null) =>
        Console.WriteLine(deviceName is null ? $"[{jobName}] {phase}" : $"[{jobName}] {phase} @ {deviceName}");

    public void JobQueued(string jobName, string reason) => Console.WriteLine($"[{jobName}] queued: {reason}");

    public void JobSkipped(string jobName, string reason) => Console.WriteLine($"[{jobName}] skipped: {reason}");

    public void JobCompleted(string jobName, bool pushed) => Console.WriteLine($"[{jobName}] completed (pushed={pushed})");

    public void JobFailed(string jobName, Exception exception) => Console.Error.WriteLine($"[{jobName}] FAILED: {exception.Message}");

    public void JobCancelled(string jobName) => Console.WriteLine($"[{jobName}] stopped");

    public void MergeConflictsDetected(string jobName, string deviceName, int conflictCount) =>
        Console.WriteLine($"[{jobName}] {conflictCount} conflict(s) with '{deviceName}' resolved (newer-wins; losers backed up)");

    public void WatchStarted(string jobName, string deviceName, bool liveWatch) =>
        Console.WriteLine($"[{jobName}] watching '{deviceName}' ({(liveWatch ? "live" : "polling")})");

    public void WatchDegraded(string jobName, string deviceName, string reason) =>
        Console.WriteLine($"[{jobName}] watch on '{deviceName}' fell back to polling: {reason}");

    public void WatchStopped(string jobName, string deviceName) =>
        Console.WriteLine($"[{jobName}] stopped watching '{deviceName}'");

    public void ChangeDetected(string jobName, string deviceName) =>
        Console.WriteLine($"[{jobName}] change detected on '{deviceName}'");
}
