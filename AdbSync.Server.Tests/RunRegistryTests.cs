using AdbSync.Contracts;
using AdbSync.Server.Api;

namespace AdbSync.Server.Tests;

public class RunRegistryTests
{
    [Fact]
    public void WithContext_KeepsFinishedOutcomeAndTakesRequestContextFromActiveRun()
    {
        var id = Guid.NewGuid();
        var client = Guid.NewGuid();
        var queued = DateTimeOffset.UtcNow.AddMinutes(-5);
        var finished = new RunDto(id, Guid.NewGuid(), "Job", "completed", "normal", "manual", null, queued.AddSeconds(3), queued.AddSeconds(3),
            queued.AddMinutes(4), null, null, 5, 1, 0, 100, 1, 1);
        var active = new RunDto(id, finished.JobId, "Job", "running", "dryRun", "schedule", client, queued, queued.AddSeconds(3), null, null, null,
            0, 0, 0, 0, null, null);

        var merged = RunRegistry.WithContext(finished, active);

        Assert.Equal("completed", merged.Status);
        Assert.Equal(5, merged.FilesCopied);
        Assert.Equal("dryRun", merged.Mode);
        Assert.Equal("schedule", merged.Trigger);
        Assert.Equal(client, merged.RequestedBy);
        Assert.Equal(queued, merged.QueuedAt);
    }
}
