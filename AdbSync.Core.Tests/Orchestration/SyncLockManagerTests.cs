using AdbSync.Core.Services.Orchestration;

namespace AdbSync.Core.Tests.Orchestration;

public class SyncLockManagerTests : IDisposable
{
    private readonly string _projectRoot = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly SyncLockManager _manager = new();

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot))
            Directory.Delete(_projectRoot, recursive: true);
    }

    private string StagingRoot => SyncLockManager.GetStagingRoot(_projectRoot);
    private string LockPath => SyncLockManager.GetLockPath(_projectRoot);

    [Fact]
    public async Task TryAcquireAsync_WhileHeld_ReturnsNull()
    {
        await using var held = await _manager.TryAcquireAsync(_projectRoot);
        Assert.NotNull(held);

        Assert.Null(await _manager.TryAcquireAsync(_projectRoot));
    }

    [Fact]
    public async Task TryAcquireAsync_AfterRelease_CanBeReacquired()
    {
        var first = await _manager.TryAcquireAsync(_projectRoot);
        await first!.DisposeAsync();

        await using var second = await _manager.TryAcquireAsync(_projectRoot);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task TryAcquireAsync_LockFileLeftByADeadRun_IsReclaimedImmediately()
    {
        Directory.CreateDirectory(StagingRoot);
        File.WriteAllText(LockPath, $"pid=1\nstart={DateTimeOffset.UtcNow:o}");

        await using var handle = await _manager.TryAcquireAsync(_projectRoot);

        Assert.NotNull(handle);
    }

    [Fact]
    public async Task TryAcquireAsync_ClearsLeftoverStagingButKeepsTheLockFile()
    {
        var leftover = Path.Combine(StagingRoot, "DeviceA", "half-pulled.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllText(leftover, "partial");

        await using var handle = await _manager.TryAcquireAsync(_projectRoot);

        Assert.False(Directory.Exists(Path.Combine(StagingRoot, "DeviceA")));
        Assert.True(File.Exists(LockPath));
    }

    [Fact]
    public async Task TryAcquireAsync_ReadOnlyLeftoverLock_IsReclaimed()
    {
        Directory.CreateDirectory(StagingRoot);
        File.WriteAllText(LockPath, "");
        File.SetAttributes(LockPath, FileAttributes.ReadOnly);

        await using var handle = await _manager.TryAcquireAsync(_projectRoot);

        Assert.NotNull(handle);
    }

    [Fact]
    public async Task TryAcquireAsync_ManyConcurrentAcquirers_ExactlyOneWins()
    {
        var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => _manager.TryAcquireAsync(_projectRoot))));

        var winners = attempts.Where(h => h is not null).ToList();
        Assert.Single(winners);
        await winners[0]!.DisposeAsync();
    }
}
