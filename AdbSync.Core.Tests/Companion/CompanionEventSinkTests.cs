using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Companion;
using AdbSync.Core.Services.Orchestration;

namespace AdbSync.Core.Tests.Companion;

public class CompanionEventSinkTests
{
    private readonly List<CompanionEvent> _published = [];
    private readonly CompanionEventSink _sink;

    public CompanionEventSinkTests() => _sink = new CompanionEventSink(_published.Add);

    [Fact]
    public void PhaseChange_PublishesAndTracksJobForHeartbeats()
    {
        _sink.PhaseChanged("Job", SyncPhase.Pull, "Phone");

        var e = Assert.Single(_published);
        Assert.Equal(new CompanionEvent(CompanionEventKind.Phase, "Job", "Pull", "Phone"), e);
        var heartbeat = Assert.Single(_sink.ActiveHeartbeats());
        Assert.Equal(CompanionEventKind.Heartbeat, heartbeat.Kind);
        Assert.Equal("Pull", heartbeat.Phase);
    }

    [Fact]
    public void Completion_StopsHeartbeats()
    {
        _sink.PhaseChanged("Job", SyncPhase.Push, "Phone");
        _sink.JobCompleted("Job", pushed: true);

        Assert.Equal(new CompanionEvent(CompanionEventKind.Completed, "Job", Pushed: true), _published[^1]);
        Assert.Empty(_sink.ActiveHeartbeats());
    }

    [Fact]
    public void AlreadyRunningSkip_DoesNotEndTheActiveRun()
    {
        _sink.PhaseChanged("Job", SyncPhase.Pull, "Phone");
        _sink.JobSkipped("Job", SyncJobRunner.AlreadyRunningReason);

        Assert.Single(_published);
        Assert.Single(_sink.ActiveHeartbeats());
    }

    [Fact]
    public void OtherSkip_EndsTheActiveRun()
    {
        _sink.PhaseChanged("Job", SyncPhase.PreConnect);
        _sink.JobSkipped("Job", "com.game is currently running on Phone");

        Assert.Equal(CompanionEventKind.Skipped, _published[^1].Kind);
        Assert.Empty(_sink.ActiveHeartbeats());
    }

    [Fact]
    public void SkipOrCancel_ForInactiveJob_PublishesNothing()
    {
        _sink.JobSkipped("Job", "watch stopped");
        _sink.JobCancelled("Job");

        Assert.Empty(_published);
    }

    [Fact]
    public void DuplicateCancel_PublishesOnce()
    {
        _sink.PhaseChanged("Job", SyncPhase.Pull, "Phone");
        _sink.JobCancelled("Job");
        _sink.JobCancelled("Job");

        Assert.Single(_published, e => e.Kind == CompanionEventKind.Cancelled);
    }

    [Fact]
    public void WaitingForAppClose_ReleasesTheActiveRun_AndIsIgnoredWhenInactive()
    {
        _sink.PhaseChanged("Idle", SyncPhase.WaitingForAppClose, "Phone");
        Assert.Empty(_published);

        _sink.PhaseChanged("Job", SyncPhase.PreConnect);
        _sink.PhaseChanged("Job", SyncPhase.WaitingForAppClose, "Phone");

        Assert.Equal(CompanionEventKind.Skipped, _published[^1].Kind);
        Assert.Empty(_sink.ActiveHeartbeats());
    }

    [Fact]
    public void Failure_PublishesEvenWithoutPriorPhase()
    {
        _sink.JobFailed("Job", new InvalidOperationException("Projects directory is not configured"));

        var e = Assert.Single(_published);
        Assert.Equal(CompanionEventKind.Failed, e.Kind);
        Assert.Equal("Projects directory is not configured", e.Message);
    }
}
