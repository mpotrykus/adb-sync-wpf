using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Devices;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Tests.Orchestration.Fakes;

namespace AdbSync.Core.Tests.Orchestration;

public class DeviceSnapshotServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _projectsDirectory;

    public DeviceSnapshotServiceTests()
    {
        _projectsDirectory = Path.Combine(_root, "projects");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string DeviceFolder(string name) => Path.Combine(_root, "devices", name);

    [Fact]
    public async Task CreateSnapshotAsync_PrunesCheckpointsOlderThanRetentionButKeepsRecentOnes()
    {
        Directory.CreateDirectory(Path.Combine(DeviceFolder("DeviceA")));
        File.WriteAllText(Path.Combine(DeviceFolder("DeviceA"), "a.txt"), "content");

        var job = new SyncJobConfig
        {
            Name = "JobSnap",
            CheckpointRetentionDays = 30,
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var settings = new GlobalSettings { ProjectsDirectory = _projectsDirectory };
        var service = new DeviceSnapshotService(
            new FakeDeviceResolver(), new FakeAdbTransferEngine(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") }));

        // Seed a stale checkpoint folder directly (older than retention) alongside a fresh one.
        var checkpointsRoot = Path.Combine(_projectsDirectory, "JobSnap", "checkpoints");
        Directory.CreateDirectory(checkpointsRoot);
        var staleDir = Path.Combine(checkpointsRoot, "2020-01-01_00-00-00");
        Directory.CreateDirectory(staleDir);
        var freshDir = Path.Combine(checkpointsRoot, DateTimeOffset.Now.AddDays(-1).ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(freshDir);

        await service.CreateSnapshotAsync(job, [device], settings);

        Assert.False(Directory.Exists(staleDir), "Expected the checkpoint older than retention to be pruned.");
        Assert.True(Directory.Exists(freshDir), "Expected the recent checkpoint to survive.");
    }

    [Fact]
    public async Task CreateSnapshotAsync_JobOverridesRetention_UsesJobValueInsteadOfGlobalDefault()
    {
        Directory.CreateDirectory(DeviceFolder("DeviceA"));
        File.WriteAllText(Path.Combine(DeviceFolder("DeviceA"), "a.txt"), "content");

        var job = new SyncJobConfig
        {
            Name = "JobSnapOverride",
            CheckpointRetentionDays = 2, // tighter than the global default
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var settings = new GlobalSettings { ProjectsDirectory = _projectsDirectory, CheckpointRetentionDays = 30 };
        var service = new DeviceSnapshotService(
            new FakeDeviceResolver(), new FakeAdbTransferEngine(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") }));

        var checkpointsRoot = Path.Combine(_projectsDirectory, "JobSnapOverride", "checkpoints");
        Directory.CreateDirectory(checkpointsRoot);
        // 5 days old: within the global 30-day retention, but past the job's 2-day override.
        var midAgeDir = Path.Combine(checkpointsRoot, DateTimeOffset.Now.AddDays(-5).ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(midAgeDir);

        await service.CreateSnapshotAsync(job, [device], settings);

        Assert.False(Directory.Exists(midAgeDir), "Expected the job's tighter retention override to prune this checkpoint.");
    }
}
