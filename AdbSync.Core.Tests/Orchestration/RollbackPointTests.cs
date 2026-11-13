using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Merge;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Orchestration.RunHistory;
using AdbSync.Core.Tests.Orchestration.Fakes;

namespace AdbSync.Core.Tests.Orchestration;

public class RollbackPointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _projectsDirectory;
    private readonly AppPaths _appPaths;
    private readonly GlobalSettings _settings;
    private readonly RollbackPointStore _store = new();
    private readonly List<DeviceConfig> _devices = [new() { Name = "DeviceA", Serial = "DeviceA" }];
    private readonly SyncJobConfig _job = new()
    {
        Name = "Job",
        Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
    };

    public RollbackPointTests()
    {
        _projectsDirectory = Path.Combine(_root, "projects");
        _appPaths = new AppPaths(Path.Combine(_root, "appdata"));
        _settings = new GlobalSettings { ProjectsDirectory = _projectsDirectory, RollbackPointsToKeep = 2 };
        ProjectFolders.AdoptRoot(_projectsDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string DeviceFolder => Path.Combine(_root, "devices", "DeviceA");
    private string ProjectRoot => Path.Combine(_projectsDirectory, "Job");
    private string MasterFile(string name) => Path.Combine(ProjectRoot, "master", name);

    private FakeAdbTransferEngine Transfer => new(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder });

    private SyncJobRunner CreateRunner(ITwoWayMergeEngine? merge = null) => new(
        new FakeDeviceResolver(), new FakeAppRunningGuard(), new SyncLockManager(), Transfer, merge ?? new TwoWayMergeEngine(),
        new ManifestStore(_appPaths), new PushSafetyGuard(_appPaths), new CheckpointManager(_appPaths),
        NullSyncEventSink.Instance, new RunHistoryStore(_appPaths), rollbackPoints: _store);

    private void WriteDeviceFile(string name, string content)
    {
        Directory.CreateDirectory(DeviceFolder);
        File.WriteAllText(Path.Combine(DeviceFolder, name), content);
    }

    [Fact]
    public async Task RunAsync_SyncThatChangesFiles_CapturesPreMergeMasterAndDeviceFolder()
    {
        WriteDeviceFile("old.txt", "v1");
        await CreateRunner().RunAsync(_job, 0, _devices, _settings, resumeFrom: null);

        WriteDeviceFile("new.txt", "v2");
        await CreateRunner().RunAsync(_job, 0, _devices, _settings, resumeFrom: null);

        var newest = _store.List(ProjectRoot).First();
        Assert.Equal(["DeviceA"], newest.DeviceNames);
        Assert.True(File.Exists(Path.Combine(newest.Path, "master", "old.txt")));
        Assert.False(File.Exists(Path.Combine(newest.Path, "master", "new.txt")));
        Assert.True(File.Exists(Path.Combine(newest.Path, "devices", "DeviceA", "new.txt")));
    }

    [Fact]
    public async Task RunAsync_SyncWithNothingToDo_LeavesNoRollbackPoint()
    {
        WriteDeviceFile("a.txt", "v1");
        await CreateRunner().RunAsync(_job, 0, _devices, _settings, resumeFrom: null);
        var before = _store.List(ProjectRoot).Count;

        await CreateRunner().RunAsync(_job, 0, _devices, _settings, resumeFrom: null);

        Assert.Equal(before, _store.List(ProjectRoot).Count);
    }

    [Fact]
    public async Task RunAsync_SyncWithNothingToDo_CopiesNothingIntoTheRollbackFolder()
    {
        WriteDeviceFile("a.txt", "v1");
        await CreateRunner().RunAsync(_job, 0, _devices, _settings, resumeFrom: null);
        Directory.Delete(RollbackPointStore.RootFor(ProjectRoot), recursive: true);

        await CreateRunner().RunAsync(_job, 0, _devices, _settings, resumeFrom: null);

        Assert.False(Directory.Exists(RollbackPointStore.RootFor(ProjectRoot)));
    }

    [Fact]
    public async Task RunAsync_PreCheckFails_StillCapturesARollbackPoint()
    {
        WriteDeviceFile("old.txt", "v1");
        await CreateRunner().RunAsync(_job, 0, _devices, _settings, resumeFrom: null);

        WriteDeviceFile("new.txt", "v2");
        await CreateRunner(new FailingDryRunMergeEngine()).RunAsync(_job, 0, _devices, _settings, resumeFrom: null);

        Assert.Equal(1, _store.List(ProjectRoot).Count(p => File.Exists(Path.Combine(p.Path, "devices", "DeviceA", "new.txt"))));
    }

    [Fact]
    public async Task RunAsync_IdleDeviceBeforeChangingDevice_IsCapturedWithThePoint()
    {
        var folderB = Path.Combine(_root, "devices", "DeviceB");
        var devices = new List<DeviceConfig> { new() { Name = "DeviceA", Serial = "DeviceA" }, new() { Name = "DeviceB", Serial = "DeviceB" } };
        var job = new SyncJobConfig
        {
            Name = "Job",
            Devices =
            [
                new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" },
                new JobDeviceBinding { DeviceName = "DeviceB", RemotePath = "/sdcard/app" },
            ],
        };
        SyncJobRunner Runner() => new(
            new FakeDeviceResolver(), new FakeAppRunningGuard(), new SyncLockManager(),
            new FakeAdbTransferEngine(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder, ["DeviceB"] = folderB }),
            new TwoWayMergeEngine(), new ManifestStore(_appPaths), new PushSafetyGuard(_appPaths), new CheckpointManager(_appPaths),
            NullSyncEventSink.Instance, new RunHistoryStore(_appPaths), rollbackPoints: _store);
        WriteDeviceFile("shared.txt", "v1");
        Directory.CreateDirectory(folderB);
        File.WriteAllText(Path.Combine(folderB, "shared.txt"), "v1");
        await Runner().RunAsync(job, 0, devices, _settings, resumeFrom: null);

        File.WriteAllText(Path.Combine(folderB, "only-b.txt"), "v2");
        await Runner().RunAsync(job, 0, devices, _settings, resumeFrom: null);

        var newest = _store.List(ProjectRoot).First();
        Assert.Equal(["DeviceA", "DeviceB"], newest.DeviceNames);
    }

    private sealed class FailingDryRunMergeEngine : ITwoWayMergeEngine
    {
        private readonly TwoWayMergeEngine _inner = new();

        public Task<MergeResult> MergeAsync(string stagingPath, string masterPath, SyncManifest manifest, MergeOptions options, CancellationToken ct = default) =>
            options.DryRun ? throw new IOException("pre-check boom") : _inner.MergeAsync(stagingPath, masterPath, manifest, options, ct);
    }

    [Fact]
    public async Task RunAsync_MoreChangingSyncsThanKeep_PrunesOldestPoints()
    {
        for (var i = 0; i < 4; i++)
        {
            WriteDeviceFile($"f{i}.txt", "x");
            await CreateRunner().RunAsync(_job, 0, _devices, _settings, resumeFrom: null);
            await Task.Delay(1100);
        }

        Assert.Equal(2, _store.List(ProjectRoot).Count);
    }

    [Fact]
    public async Task RunAsync_RollbackPointsToKeepZero_CapturesNothing()
    {
        _settings.RollbackPointsToKeep = 0;
        WriteDeviceFile("a.txt", "v1");

        await CreateRunner().RunAsync(_job, 0, _devices, _settings, resumeFrom: null);

        Assert.Empty(_store.List(ProjectRoot));
    }

    [Fact]
    public async Task RestoreAsync_PutsMasterAndDeviceBackToPreSyncState()
    {
        WriteDeviceFile("keep.txt", "v1");
        await CreateRunner().RunAsync(_job, 0, _devices, _settings, resumeFrom: null);
        WriteDeviceFile("keep.txt", "v2-bad");
        await Task.Delay(1100);
        await CreateRunner().RunAsync(_job, 0, _devices, _settings, resumeFrom: null);
        Assert.Equal("v2-bad", File.ReadAllText(MasterFile("keep.txt")));

        var point = _store.List(ProjectRoot).OrderByDescending(p => p.CreatedAt).First();
        var service = new RollbackService(
            new FakeDeviceResolver(), Transfer, new ManifestStore(_appPaths), new PushSafetyGuard(_appPaths), new SyncLockManager(), _store);

        var result = await service.RestoreAsync(_job, _devices, _settings, point.Path);

        Assert.Equal(1, result.DeviceCount);
        Assert.Equal("v1", File.ReadAllText(MasterFile("keep.txt")));
        Assert.Equal("v2-bad", File.ReadAllText(Path.Combine(DeviceFolder, "keep.txt")));
        Assert.Contains(_store.List(ProjectRoot), p => p.DeviceNames.Count == 0 && File.Exists(Path.Combine(p.Path, "master", "keep.txt")));
    }
}
