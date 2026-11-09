using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Companion;

namespace AdbSync.Core.Tests.Companion;

public class SyncStalenessTests
{
    private static readonly string[] Devices = ["S23", "S9"];

    private static JobStatus Status(
        bool running = false, string? phase = null, string[]? pending = null, string? blocked = null) =>
        new("Job", running, phase, [], null, false, null, pending ?? [], blocked);

    private static SyncCheckpoint Checkpoint(SyncPhase phase, params string[] completed) =>
        new(1, DateTimeOffset.Now, 0, "Job", phase, completed, []);

    [Fact]
    public void Idle_NothingPending_IsInSync() =>
        Assert.Empty(SyncStaleness.Assess(Devices, Status(), null));

    [Fact]
    public void PendingChangesOnOneDevice_MakeTheOthersStale()
    {
        var stale = SyncStaleness.Assess(Devices, Status(pending: ["S9"]), null);

        var only = Assert.Single(stale);
        Assert.Equal("S23", only.Device);
        Assert.Contains("S9", only.Reason);
    }

    [Fact]
    public void ChangesOnEveryDevice_MakeEveryDeviceStale()
    {
        var stale = SyncStaleness.Assess(Devices, Status(pending: ["S23", "S9"]), null);

        Assert.Equal(["S23", "S9"], stale.Select(s => s.Device));
    }

    [Fact]
    public void AppOpenOnADevice_MakesTheOthersStale()
    {
        var stale = SyncStaleness.Assess(Devices, Status(running: true, phase: "WaitingForAppClose", blocked: "S23"), null);

        var only = Assert.Single(stale);
        Assert.Equal("S9", only.Device);
        Assert.Contains("app open", only.Reason);
    }

    [Fact]
    public void ActiveSync_ReportsNothing() =>
        Assert.Empty(SyncStaleness.Assess(Devices, Status(running: true, phase: "Pull", pending: ["S9"]), null));

    [Fact]
    public void QueuedSync_StillReportsPendingChanges() =>
        Assert.Single(SyncStaleness.Assess(Devices, Status(running: true, phase: "Queued", pending: ["S9"]), null));

    [Fact]
    public void InterruptedPush_FlagsOnlyDevicesNotYetPushedTo()
    {
        var stale = SyncStaleness.Assess(Devices, Status(), Checkpoint(SyncPhase.Push, "S23"));

        Assert.Equal("S9", Assert.Single(stale).Device);
    }

    [Fact]
    public void InterruptedPull_FlagsEveryDevice() =>
        Assert.Equal(2, SyncStaleness.Assess(Devices, Status(), Checkpoint(SyncPhase.Pull, "S23")).Count);

    [Fact]
    public void FirstReasonWins_WhenSeveralApply()
    {
        var stale = SyncStaleness.Assess(Devices, Status(pending: ["S9"], blocked: "S9"), Checkpoint(SyncPhase.Push));

        Assert.Contains("haven't synced yet", stale.Single(s => s.Device == "S23").Reason);
    }
}
