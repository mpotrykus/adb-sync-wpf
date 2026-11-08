using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;
using System.Collections.Concurrent;

namespace AdbSync.Core.Services.Companion;

/// <summary>Translates run events into <see cref="CompanionEvent"/>s for the phone and tracks which jobs are
/// active, so the transport can heartbeat them. The phone holds wake/Wi-Fi locks only while a job is active.</summary>
public sealed class CompanionEventSink(Action<CompanionEvent> publish) : ISyncEventSink
{
    private readonly ConcurrentDictionary<string, CompanionEvent> _active = new(StringComparer.Ordinal);

    public IReadOnlyList<CompanionEvent> ActiveHeartbeats() =>
        _active.Values.Select(e => e with { Kind = CompanionEventKind.Heartbeat }).ToList();

    public void PhaseChanged(string jobName, SyncPhase phase, string? deviceName = null)
    {
        if (phase == SyncPhase.WaitingForAppClose)
        {
            // The user is actively using the app on the phone and this wait can last hours; holding locks
            // (and the time-limited dataSync foreground service) for it would be pointless.
            if (_active.ContainsKey(jobName))
                Finish(new(CompanionEventKind.Skipped, jobName, Message: "Waiting for app to close"));
            return;
        }

        Start(new(CompanionEventKind.Phase, jobName, phase.ToString(), deviceName));
    }

    public void JobQueued(string jobName, string reason) =>
        Start(new(CompanionEventKind.Queued, jobName, Message: reason));

    public void JobSkipped(string jobName, string reason)
    {
        // A duplicate trigger bouncing off the project lock says nothing about the run that holds it.
        if (reason != SyncJobRunner.AlreadyRunningReason && _active.ContainsKey(jobName))
            Finish(new(CompanionEventKind.Skipped, jobName, Message: reason));
    }

    public void JobCompleted(string jobName, bool pushed) =>
        Finish(new(CompanionEventKind.Completed, jobName, Pushed: pushed));

    public void JobFailed(string jobName, Exception exception) =>
        Finish(new(CompanionEventKind.Failed, jobName, Message: exception.Message));

    public void JobCancelled(string jobName)
    {
        if (_active.ContainsKey(jobName))
            Finish(new(CompanionEventKind.Cancelled, jobName));
    }

    public void MergeConflictsDetected(string jobName, string deviceName, int conflictCount) { }
    public void WatchStarted(string jobName, string deviceName, bool liveWatch) { }
    public void WatchDegraded(string jobName, string deviceName, string reason) { }
    public void WatchStopped(string jobName, string deviceName) { }
    public void ChangeDetected(string jobName, string deviceName) { }

    private void Start(CompanionEvent e)
    {
        _active[e.JobName] = e;
        publish(e);
    }

    private void Finish(CompanionEvent e)
    {
        _active.TryRemove(e.JobName, out _);
        publish(e);
    }
}
