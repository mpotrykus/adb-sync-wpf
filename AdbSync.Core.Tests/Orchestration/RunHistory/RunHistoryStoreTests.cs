using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Orchestration.RunHistory;

namespace AdbSync.Core.Tests.Orchestration.RunHistory;

public class RunHistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly RunHistoryStore _store;

    public RunHistoryStoreTests()
    {
        _store = new RunHistoryStore(new AppPaths(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static JobRunRecord MakeRecord(string jobName, DateTimeOffset startedAt) => new(
        Guid.NewGuid(), jobName, startedAt, startedAt.AddSeconds(1), JobRunOutcome.Completed,
        ErrorMessage: null, FilesCopied: 1, FilesDeleted: 0, ErrorCount: 0, BytesCopied: 10, PullDuration: null, PushDuration: null);

    [Fact]
    public async Task SaveRunAsync_MoreRunsThanMaxRuns_TrimsToMostRecent()
    {
        var baseTime = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
            await _store.SaveRunAsync(MakeRecord("Job", baseTime.AddMinutes(i)), $"log-{i}", maxRuns: 3);

        var runs = await _store.ListRunsAsync("Job");

        Assert.Equal(3, runs.Count);
        // Most recent first, and the trim must have kept the newest 3 (minutes 2, 3, 4), not the oldest.
        Assert.Equal(baseTime.AddMinutes(4), runs[0].StartedAt);
        Assert.Equal(baseTime.AddMinutes(3), runs[1].StartedAt);
        Assert.Equal(baseTime.AddMinutes(2), runs[2].StartedAt);
    }

    [Fact]
    public async Task SaveRunAsync_DifferentMaxRunsAcrossSaves_UsesTheLatestValue()
    {
        var baseTime = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
            await _store.SaveRunAsync(MakeRecord("Job", baseTime.AddMinutes(i)), $"log-{i}", maxRuns: 10);

        Assert.Equal(5, (await _store.ListRunsAsync("Job")).Count);

        // A later save with a tighter cap re-trims the whole job's history down immediately.
        await _store.SaveRunAsync(MakeRecord("Job", baseTime.AddMinutes(5)), "log-5", maxRuns: 2);

        var runs = await _store.ListRunsAsync("Job");
        Assert.Equal(2, runs.Count);
        Assert.Equal(baseTime.AddMinutes(5), runs[0].StartedAt);
    }
}
