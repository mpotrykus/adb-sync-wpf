using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Merge;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Orchestration.RunHistory;
using AdbSync.Core.Tests.Orchestration.Fakes;
using NSubstitute;

namespace AdbSync.Core.Tests.Orchestration;

public class ConflictResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _appPaths;
    private readonly GlobalSettings _settings;
    private readonly PendingConflictStore _pending;
    private readonly ManifestStore _manifests;
    private readonly SyncJobConfig _job;
    private readonly DeviceConfig _device = new() { Name = "DeviceA", Serial = "DeviceA" };
    private readonly SyncJobRunner _runner;
    private readonly ConflictResolver _resolver;

    public ConflictResolutionTests()
    {
        _appPaths = new AppPaths(Path.Combine(_root, "appdata"));
        _settings = new GlobalSettings { ProjectsDirectory = Path.Combine(_root, "projects"), ConflictPolicy = ConflictPolicy.Ask };
        ProjectFolders.AdoptRoot(_settings.ProjectsDirectory);
        _job = new SyncJobConfig { Name = "Job", Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }] };
        _pending = new PendingConflictStore(_appPaths);
        _manifests = new ManifestStore(_appPaths);
        _runner = new SyncJobRunner(
            new FakeDeviceResolver(), new FakeAppRunningGuard(false), new SyncLockManager(),
            new FakeAdbTransferEngine(new Dictionary<string, string> { ["DeviceA"] = DeviceDir }),
            new TwoWayMergeEngine(), _manifests, new PushSafetyGuard(_appPaths), new CheckpointManager(_appPaths),
            NullSyncEventSink.Instance, new RunHistoryStore(_appPaths), pendingConflicts: _pending);

        var configStore = Substitute.For<IAppConfigStore>();
        configStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AppConfig { Settings = _settings, Jobs = [_job], Devices = [_device] });
        _resolver = new ConflictResolver(configStore, _pending, _manifests, new SyncLockManager());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string DeviceDir => Path.Combine(_root, "device");
    private string DeviceFile => Path.Combine(DeviceDir, "save.dat");
    private string MasterFile => Path.Combine(_settings.ProjectsDirectory, "Job", "master", "save.dat");

    private Task RunAsync() => _runner.RunAsync(_job, 0, [_device], _settings, resumeFrom: null);

    private static void Write(string path, string content, DateTime modifiedUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
    }

    private async Task<PendingConflict> CreateConflictAsync()
    {
        Write(DeviceFile, "v1", DateTime.UtcNow.AddHours(-3));
        await RunAsync();
        await RunAsync();
        Write(MasterFile, "master-v2", DateTime.UtcNow.AddHours(-2));
        Write(DeviceFile, "device-v2", DateTime.UtcNow.AddHours(-1));
        await RunAsync();
        return Assert.Single(await _pending.ListAsync());
    }

    [Fact]
    public async Task Ask_ConflictingFile_IsHeldAndLeftUntouchedOnBothSides()
    {
        var conflict = await CreateConflictAsync();

        Assert.Equal("save.dat", conflict.RelativePath);
        Assert.Equal("master-v2", File.ReadAllText(MasterFile));
        Assert.Equal("device-v2", File.ReadAllText(DeviceFile));

        await RunAsync();
        Assert.Single(await _pending.ListAsync());
        Assert.Equal("device-v2", File.ReadAllText(DeviceFile));
    }

    [Fact]
    public async Task ResolveDevice_CopiesDeviceVersionIntoMasterAndClearsConflict()
    {
        var conflict = await CreateConflictAsync();

        var job = await _resolver.ResolveAsync(conflict.Id, ConflictChoice.Device);
        await RunAsync();

        Assert.Equal("Job", job);
        Assert.Empty(await _pending.ListAsync());
        Assert.Equal("device-v2", File.ReadAllText(MasterFile));
        Assert.Equal("device-v2", File.ReadAllText(DeviceFile));
    }

    [Fact]
    public async Task ResolveMaster_PushesMasterVersionOverTheDevice()
    {
        var conflict = await CreateConflictAsync();

        await _resolver.ResolveAsync(conflict.Id, ConflictChoice.Master);
        await RunAsync();

        Assert.Empty(await _pending.ListAsync());
        Assert.Equal("master-v2", File.ReadAllText(MasterFile));
        Assert.Equal("master-v2", File.ReadAllText(DeviceFile));
    }

    [Fact]
    public async Task ResolveBoth_KeepsMasterAndSavesDeviceVersionAsASeparateFile()
    {
        var conflict = await CreateConflictAsync();

        await _resolver.ResolveAsync(conflict.Id, ConflictChoice.Both);
        await RunAsync();

        Assert.Equal("master-v2", File.ReadAllText(DeviceFile));
        var sibling = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(MasterFile)!, "save (DeviceA *).dat"));
        Assert.Equal("device-v2", File.ReadAllText(sibling));
    }

    [Fact]
    public async Task ResolveNewest_PicksTheLaterModifiedSide()
    {
        var conflict = await CreateConflictAsync();

        await _resolver.ResolveAsync(conflict.Id, ConflictChoice.Newest);
        await RunAsync();

        Assert.Equal("device-v2", File.ReadAllText(MasterFile));
    }

    [Fact]
    public async Task DeviceDeletedWhileMasterEdited_ResolvingForMasterRestoresTheFileOnTheDevice()
    {
        Write(DeviceFile, "v1", DateTime.UtcNow.AddHours(-3));
        await RunAsync();
        await RunAsync();
        Write(MasterFile, "master-v2", DateTime.UtcNow.AddHours(-2));
        File.Delete(DeviceFile);
        await RunAsync();
        var conflict = Assert.Single(await _pending.ListAsync());
        Assert.Null(conflict.DeviceModifiedUtc);

        await _resolver.ResolveAsync(conflict.Id, ConflictChoice.Master);
        await RunAsync();

        Assert.Equal("master-v2", File.ReadAllText(DeviceFile));
    }

    [Fact]
    public async Task ConflictResolvedByHandOnTheDevice_DropsOutOfThePendingList()
    {
        await CreateConflictAsync();
        Write(DeviceFile, "master-v2", DateTime.UtcNow.AddHours(-2));

        await RunAsync();

        Assert.Empty(await _pending.ListAsync());
    }

    [Fact]
    public async Task NewestWinsPolicy_StillResolvesAutomatically()
    {
        _settings.ConflictPolicy = ConflictPolicy.NewestWins;
        Write(DeviceFile, "v1", DateTime.UtcNow.AddHours(-3));
        await RunAsync();
        await RunAsync();
        Write(MasterFile, "master-v2", DateTime.UtcNow.AddHours(-2));
        Write(DeviceFile, "device-v2", DateTime.UtcNow.AddHours(-1));

        await RunAsync();

        Assert.Empty(await _pending.ListAsync());
        Assert.Equal("device-v2", File.ReadAllText(MasterFile));
    }
}
