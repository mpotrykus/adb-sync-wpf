using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using AdbSync.Core.Services.Companion;
using AdbSync.Core.Services.Orchestration;

namespace AdbSync.Core.Tests.Companion;

public class JobStatusTrackerTests
{
    private readonly JobStatusTracker _tracker = new();
    private readonly List<JobStatus> _changes = [];

    public JobStatusTrackerTests() => _tracker.Changed += _changes.Add;

    [Fact]
    public void DevicePhases_AreTrackedPerDevice_AndClearedByJobLevelPhase()
    {
        _tracker.PhaseChanged("Job", SyncPhase.Pull, "S23");
        _tracker.PhaseChanged("Job", SyncPhase.Merge, "S9");

        var status = _tracker.Get("Job");
        Assert.True(status.Running);
        Assert.Equal([new DevicePhase("S23", "Pull"), new DevicePhase("S9", "Merge")], status.Devices);

        _tracker.PhaseChanged("Job", SyncPhase.PreConnect);
        status = _tracker.Get("Job");
        Assert.Empty(status.Devices);
        Assert.Equal("PreConnect", status.Phase);
    }

    [Fact]
    public void Completion_ReportsConflictsFromThisRun()
    {
        _tracker.PhaseChanged("Job", SyncPhase.PreConnect);
        _tracker.MergeConflictsDetected("Job", "S23", 2);
        _tracker.JobCompleted("Job", pushed: true);

        var status = _tracker.Get("Job");
        Assert.False(status.Running);
        Assert.Empty(status.Devices);
        Assert.Equal("Success (2 conflict(s) resolved)", status.LastResult);
        Assert.False(status.LastFailed);
    }

    [Fact]
    public void Failure_IsFlagged()
    {
        _tracker.PhaseChanged("Job", SyncPhase.Push, "S23");
        _tracker.JobFailed("Job", new IOException("device gone"));

        var status = _tracker.Get("Job");
        Assert.Equal("Error: device gone", status.LastResult);
        Assert.True(status.LastFailed);
    }

    [Fact]
    public void AlreadyRunningSkip_LeavesActiveRunAlone()
    {
        _tracker.PhaseChanged("Job", SyncPhase.Pull, "S23");
        _changes.Clear();

        _tracker.JobSkipped("Job", SyncJobRunner.AlreadyRunningReason);

        Assert.Empty(_changes);
        Assert.True(_tracker.Get("Job").Running);
    }

    [Fact]
    public void DuplicateCancel_RaisesOneChange()
    {
        _tracker.PhaseChanged("Job", SyncPhase.Pull, "S23");
        _changes.Clear();

        _tracker.JobCancelled("Job");
        _tracker.JobCancelled("Job");

        Assert.Single(_changes);
        Assert.Equal("Stopped", _tracker.Get("Job").LastResult);
    }

    [Fact]
    public void SeedFromHistory_OnlyFillsMissingResult()
    {
        var record = new JobRunRecord(Guid.NewGuid(), "Job", DateTimeOffset.Now, DateTimeOffset.Now,
            JobRunOutcome.Failed, "boom", 0, 0, 1, 0, null, null);

        _tracker.SeedFromHistory(record);
        Assert.Equal("Error: boom", _tracker.Get("Job").LastResult);

        _tracker.JobCompleted("Job", pushed: false);
        _tracker.SeedFromHistory(record);
        Assert.Equal("No changes", _tracker.Get("Job").LastResult);
    }

    [Fact]
    public void Get_UnknownJob_ReturnsIdleStatus()
    {
        var status = _tracker.Get("Nope");

        Assert.False(status.Running);
        Assert.Null(status.LastResult);
    }
}
