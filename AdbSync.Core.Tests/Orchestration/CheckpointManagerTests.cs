using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Config;
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
        Assert.Null(await _manager.LoadAsync("JobName"));
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTrips()
    {
        var checkpoint = new SyncCheckpoint(1, DateTimeOffset.UtcNow, 2, "JobName", SyncPhase.Push, ["DeviceA"],
            new Dictionary<string, string> { ["DeviceA"] = "192.168.0.40:5555" });

        await _manager.SaveAsync("JobName", checkpoint);
        var loaded = await _manager.LoadAsync("JobName");

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.ProjectIndex);
        Assert.Equal("JobName", loaded.ProjectName);
        Assert.Equal(SyncPhase.Push, loaded.Phase);
        Assert.Equal(["DeviceA"], loaded.CompletedDevices);
        Assert.Equal("192.168.0.40:5555", loaded.DeviceSerials["DeviceA"]);
    }

    [Fact]
    public async Task ClearAsync_RemovesCheckpointFile()
    {
        await _manager.SaveAsync("Job", new SyncCheckpoint(1, DateTimeOffset.UtcNow, 0, "Job", SyncPhase.Pull, [], []));

        await _manager.ClearAsync("Job");

        Assert.Null(await _manager.LoadAsync("Job"));
    }

    [Fact]
    public async Task ClearAsync_WhenNothingSaved_DoesNotThrow()
    {
        await _manager.ClearAsync("Job");
    }

    [Fact]
    public async Task SaveAsync_Overwrite_ReplacesPreviousCheckpoint()
    {
        await _manager.SaveAsync("Job", new SyncCheckpoint(1, DateTimeOffset.UtcNow, 0, "Job", SyncPhase.Pull, [], []));
        await _manager.SaveAsync("Job", new SyncCheckpoint(1, DateTimeOffset.UtcNow, 5, "Job", SyncPhase.Push, [], []));

        var loaded = await _manager.LoadAsync("Job");

        Assert.Equal(5, loaded!.ProjectIndex);
    }

    [Fact]
    public async Task SaveAsync_DifferentJobs_DoNotClobberEachOther()
    {
        await _manager.SaveAsync("JobA", new SyncCheckpoint(1, DateTimeOffset.UtcNow, 0, "JobA", SyncPhase.Pull, [], []));
        await _manager.SaveAsync("JobB", new SyncCheckpoint(1, DateTimeOffset.UtcNow, 1, "JobB", SyncPhase.Push, [], []));

        var loadedA = await _manager.LoadAsync("JobA");
        var loadedB = await _manager.LoadAsync("JobB");

        Assert.Equal(SyncPhase.Pull, loadedA!.Phase);
        Assert.Equal(SyncPhase.Push, loadedB!.Phase);
    }

    [Fact]
    public async Task ClearAsync_OnOneJob_LeavesOtherJobsCheckpointIntact()
    {
        await _manager.SaveAsync("JobA", new SyncCheckpoint(1, DateTimeOffset.UtcNow, 0, "JobA", SyncPhase.Pull, [], []));
        await _manager.SaveAsync("JobB", new SyncCheckpoint(1, DateTimeOffset.UtcNow, 1, "JobB", SyncPhase.Push, [], []));

        await _manager.ClearAsync("JobA");

        Assert.Null(await _manager.LoadAsync("JobA"));
        Assert.NotNull(await _manager.LoadAsync("JobB"));
    }

    [Fact]
    public async Task LoadAllAsync_NoCheckpoints_ReturnsEmpty()
    {
        Assert.Empty(await _manager.LoadAllAsync());
    }

    [Fact]
    public async Task LoadAllAsync_ReturnsEveryJobsCheckpoint()
    {
        await _manager.SaveAsync("JobA", new SyncCheckpoint(1, DateTimeOffset.UtcNow, 0, "JobA", SyncPhase.Pull, [], []));
        await _manager.SaveAsync("JobB", new SyncCheckpoint(1, DateTimeOffset.UtcNow, 1, "JobB", SyncPhase.Push, [], []));

        var all = await _manager.LoadAllAsync();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, c => c.ProjectName == "JobA" && c.Phase == SyncPhase.Pull);
        Assert.Contains(all, c => c.ProjectName == "JobB" && c.Phase == SyncPhase.Push);
    }
}
