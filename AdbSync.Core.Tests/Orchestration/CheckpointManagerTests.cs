using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;

namespace AdbSync.Core.Tests.Orchestration;

public class CheckpointManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly CheckpointManager _manager;

    public CheckpointManagerTests() => _manager = new CheckpointManager(new AppPaths(_root));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task LoadAsync_NoCheckpointSaved_ReturnsNull()
    {
        Assert.Null(await _manager.LoadAsync());
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTrips()
    {
        var checkpoint = new SyncCheckpoint(1, DateTimeOffset.UtcNow, 2, "JobName", SyncPhase.Push, 1,
            new Dictionary<string, string> { ["DeviceA"] = "192.168.0.40:5555" });

        await _manager.SaveAsync(checkpoint);
        var loaded = await _manager.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.ProjectIndex);
        Assert.Equal("JobName", loaded.ProjectName);
        Assert.Equal(SyncPhase.Push, loaded.Phase);
        Assert.Equal(1, loaded.DeviceIndex);
        Assert.Equal("192.168.0.40:5555", loaded.DeviceSerials["DeviceA"]);
    }

    [Fact]
    public async Task ClearAsync_RemovesCheckpointFile()
    {
        await _manager.SaveAsync(new SyncCheckpoint(1, DateTimeOffset.UtcNow, 0, "Job", SyncPhase.Pull, 0, []));

        await _manager.ClearAsync();

        Assert.Null(await _manager.LoadAsync());
    }

    [Fact]
    public async Task ClearAsync_WhenNothingSaved_DoesNotThrow()
    {
        await _manager.ClearAsync();
    }

    [Fact]
    public async Task SaveAsync_Overwrite_ReplacesPreviousCheckpoint()
    {
        await _manager.SaveAsync(new SyncCheckpoint(1, DateTimeOffset.UtcNow, 0, "Job", SyncPhase.Pull, 0, []));
        await _manager.SaveAsync(new SyncCheckpoint(1, DateTimeOffset.UtcNow, 5, "Job2", SyncPhase.Push, 3, []));

        var loaded = await _manager.LoadAsync();

        Assert.Equal(5, loaded!.ProjectIndex);
    }
}
