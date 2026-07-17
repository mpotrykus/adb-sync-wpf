using AdbSync.Core.Services.Orchestration;

namespace AdbSync.Core.Tests.Orchestration;

public class SyncLockManagerTests : IDisposable
{
    private readonly string _projectRoot = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly SyncLockManager _manager = new();
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(4);

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot))
            Directory.Delete(_projectRoot, recursive: true);
    }

    [Fact]
    public async Task TryAcquireAsync_NoExistingLock_AcquiresAndWritesLockFile()
    {
        var handle = await _manager.TryAcquireAsync(_projectRoot, StaleAfter);

        Assert.NotNull(handle);
        var lockPath = SyncLockManager.GetLockPath(_projectRoot);
        Assert.True(File.Exists(lockPath));
        Assert.Contains($"pid={Environment.ProcessId}", await File.ReadAllTextAsync(lockPath));
    }

    [Fact]
    public async Task TryAcquireAsync_LiveProcessHoldsFreshLock_ReturnsNull()
    {
        var lockPath = SyncLockManager.GetLockPath(_projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        await File.WriteAllTextAsync(lockPath, $"pid={Environment.ProcessId}\nstart={DateTimeOffset.UtcNow:o}");

        var handle = await _manager.TryAcquireAsync(_projectRoot, StaleAfter);

        Assert.Null(handle);
    }

    [Fact]
    public async Task TryAcquireAsync_DeadPid_Reclaims()
    {
        var lockPath = SyncLockManager.GetLockPath(_projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        await File.WriteAllTextAsync(lockPath, $"pid=999999\nstart={DateTimeOffset.UtcNow:o}");

        var handle = await _manager.TryAcquireAsync(_projectRoot, StaleAfter);

        Assert.NotNull(handle);
        Assert.Contains($"pid={Environment.ProcessId}", await File.ReadAllTextAsync(lockPath));
    }

    [Fact]
    public async Task TryAcquireAsync_LiveProcessButLockOlderThanStaleAfter_Reclaims()
    {
        var lockPath = SyncLockManager.GetLockPath(_projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        await File.WriteAllTextAsync(lockPath, $"pid={Environment.ProcessId}\nstart={DateTimeOffset.UtcNow.AddHours(-5):o}");

        var handle = await _manager.TryAcquireAsync(_projectRoot, StaleAfter);

        Assert.NotNull(handle);
    }

    [Fact]
    public async Task TryAcquireAsync_CorruptLockContent_Reclaims()
    {
        var lockPath = SyncLockManager.GetLockPath(_projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        await File.WriteAllTextAsync(lockPath, "not a valid lock file");

        var handle = await _manager.TryAcquireAsync(_projectRoot, StaleAfter);

        Assert.NotNull(handle);
    }

    [Fact]
    public async Task TryAcquireAsync_StaleReclaim_WipesExistingStagingContent()
    {
        var stagingRoot = SyncLockManager.GetStagingRoot(_projectRoot);
        var leftoverFile = Path.Combine(stagingRoot, "DeviceA", "partial.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(leftoverFile)!);
        File.WriteAllText(leftoverFile, "partial pull from a crashed run");
        await File.WriteAllTextAsync(SyncLockManager.GetLockPath(_projectRoot), $"pid=999999\nstart={DateTimeOffset.UtcNow:o}");

        await _manager.TryAcquireAsync(_projectRoot, StaleAfter);

        Assert.False(File.Exists(leftoverFile));
    }

    [Fact]
    public async Task DisposeAsync_DeletesLockFile()
    {
        var handle = await _manager.TryAcquireAsync(_projectRoot, StaleAfter);

        await handle!.DisposeAsync();

        Assert.False(File.Exists(SyncLockManager.GetLockPath(_projectRoot)));
    }

    [Fact]
    public async Task TryAcquireAsync_AfterDispose_CanBeReacquired()
    {
        var first = await _manager.TryAcquireAsync(_projectRoot, StaleAfter);
        await first!.DisposeAsync();

        var second = await _manager.TryAcquireAsync(_projectRoot, StaleAfter);

        Assert.NotNull(second);
    }
}
