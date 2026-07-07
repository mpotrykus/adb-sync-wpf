using AdbSync.Core.Orchestration;

namespace AdbSync.Cli;

public sealed class ConsoleSyncEventSink : ISyncEventSink
{
    public void PhaseChanged(string jobName, SyncPhase phase, string? deviceName = null) =>
        Console.WriteLine(deviceName is null ? $"[{jobName}] {phase}" : $"[{jobName}] {phase} @ {deviceName}");

    public void JobSkipped(string jobName, string reason) => Console.WriteLine($"[{jobName}] skipped: {reason}");

    public void JobCompleted(string jobName, bool pushed) => Console.WriteLine($"[{jobName}] completed (pushed={pushed})");

    public void JobFailed(string jobName, Exception exception) => Console.Error.WriteLine($"[{jobName}] FAILED: {exception.Message}");

    public void MergeConflictsDetected(string jobName, string deviceName, int conflictCount) =>
        Console.WriteLine($"[{jobName}] {conflictCount} conflict(s) with '{deviceName}' resolved (newer-wins; losers backed up)");
}
