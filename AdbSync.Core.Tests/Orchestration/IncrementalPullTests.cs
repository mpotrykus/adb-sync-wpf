using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Merge;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Transfer;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Orchestration.RunHistory;
using AdbSync.Core.Tests.Orchestration.Fakes;

namespace AdbSync.Core.Tests.Orchestration;

public class IncrementalPullPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Stamp = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static SyncManifest Manifest(DateTimeOffset? lastFull, params (string Path, long Size)[] entries) => new()
    {
        LastFullPullAtUtc = lastFull,
        Entries = entries.ToDictionary(e => e.Path, e => new ManifestEntry(e.Size, Stamp)),
    };

    [Fact]
    public void IsEligible_NoManifestOrNoEntriesOrNeverFullyPulled_IsFalse()
    {
        Assert.False(IncrementalPullPlanner.IsEligible(null, 7, Now));
        Assert.False(IncrementalPullPlanner.IsEligible(Manifest(Now.AddDays(-1)), 7, Now));
        Assert.False(IncrementalPullPlanner.IsEligible(Manifest(null, ("a", 1)), 7, Now));
    }

    [Fact]
    public void IsEligible_FullPullWithinIntervalOnly()
    {
        Assert.True(IncrementalPullPlanner.IsEligible(Manifest(Now.AddDays(-6), ("a", 1)), 7, Now));
        Assert.False(IncrementalPullPlanner.IsEligible(Manifest(Now.AddDays(-8), ("a", 1)), 7, Now));
        Assert.False(IncrementalPullPlanner.IsEligible(Manifest(Now.AddDays(-1), ("a", 1)), 0, Now));
    }

    [Fact]
    public void IsEligible_StampInTheFuture_IsFalse()
    {
        Assert.False(IncrementalPullPlanner.IsEligible(Manifest(Now.AddDays(1), ("a", 1)), 7, Now));
    }

    [Fact]
    public void SkipPredicate_OnlySkipsFilesMatchingBaselineOnDeviceAndMaster()
    {
        var manifest = Manifest(Stamp, ("same", 10), ("deviceChanged", 10), ("masterChanged", 10), ("masterMissing", 10));
        var master = new[]
        {
            new FileEntry("same", false, 10, Stamp),
            new FileEntry("deviceChanged", false, 10, Stamp),
            new FileEntry("masterChanged", false, 99, Stamp),
            new FileEntry("notInManifest", false, 10, Stamp),
        }.ToDictionary(e => e.RelativePath);
        var skip = IncrementalPullPlanner.BuildSkipPredicate(manifest, master);

        Assert.True(skip(new FileEntry("same", false, 10, Stamp.AddSeconds(1))));
        Assert.False(skip(new FileEntry("deviceChanged", false, 11, Stamp)));
        Assert.False(skip(new FileEntry("deviceChanged", false, 10, Stamp.AddMinutes(1))));
        Assert.False(skip(new FileEntry("masterChanged", false, 10, Stamp)));
        Assert.False(skip(new FileEntry("masterMissing", false, 10, Stamp)));
        Assert.False(skip(new FileEntry("notInManifest", false, 10, Stamp)));
        Assert.False(skip(new FileEntry("same", true, 0, Stamp)));
    }
}

public class IncrementalPullRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _projectsDirectory;
    private readonly AppPaths _appPaths;
    private readonly GlobalSettings _settings;
    private readonly FakeAdbTransferEngine _engine;

    public IncrementalPullRunnerTests()
    {
        _projectsDirectory = Path.Combine(_root, "projects");
        _appPaths = new AppPaths(Path.Combine(_root, "appdata"));
        _settings = new GlobalSettings { ProjectsDirectory = _projectsDirectory, StaleLockHours = 4, IncrementalPull = true, FullPullIntervalDays = 7 };
        ProjectFolders.AdoptRoot(_projectsDirectory);
        _engine = new FakeAdbTransferEngine(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA"), ["DeviceB"] = DeviceFolder("DeviceB") });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string DeviceFolder(string name) => Path.Combine(_root, "devices", name);
    private string MasterFile(string job, string path) => Path.Combine(_projectsDirectory, job, "master", path);

    private SyncJobRunner CreateRunner() => new(
        new FakeDeviceResolver(), new FakeAppRunningGuard(false), new SyncLockManager(), _engine, new TwoWayMergeEngine(),
        new ManifestStore(_appPaths), new PushSafetyGuard(_appPaths), new CheckpointManager(_appPaths),
        NullSyncEventSink.Instance, new RunHistoryStore(_appPaths));

    private static readonly List<DeviceConfig> Devices =
    [
        new() { Name = "DeviceA", Serial = "DeviceA" },
        new() { Name = "DeviceB", Serial = "DeviceB" },
    ];

    private static SyncJobConfig Job(string name, params string[] devices) => new()
    {
        Name = name,
        Devices = devices.Select(d => new JobDeviceBinding { DeviceName = d, RemotePath = "/sdcard/app" }).ToList(),
    };

    private void Write(string root, string relativePath, string content, int mtimeOffsetSeconds = 0)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(mtimeOffsetSeconds));
    }

    private Task<JobRunResult> Run(SyncJobConfig job, List<DeviceConfig>? devices = null, bool dryRun = false)
    {
        job.DryRun = dryRun;
        return CreateRunner().RunAsync(job, 0, devices ?? Devices, _settings, resumeFrom: null);
    }

    [Fact]
    public async Task FirstRun_IsFullPull_ThenUnchangedFilesAreNotPulledAgain()
    {
        var job = Job("J1", "DeviceA");
        Write(DeviceFolder("DeviceA"), "a.txt", "alpha");
        Write(DeviceFolder("DeviceA"), "sub/b.txt", "bravo");

        await Run(job);
        Assert.Equal(2, _engine.FilesPulledTotal);

        var second = await Run(job);
        Assert.Equal(2, _engine.FilesPulledTotal);
        Assert.Equal(JobRunOutcome.CompletedNoChanges, second.Outcome);
        Assert.Equal("alpha", File.ReadAllText(MasterFile("J1", "a.txt")));
    }

    [Fact]
    public async Task ChangedDeviceFile_IsPulledAndMergedAlone()
    {
        var job = Job("J2", "DeviceA");
        Write(DeviceFolder("DeviceA"), "a.txt", "alpha");
        Write(DeviceFolder("DeviceA"), "b.txt", "bravo");
        await Run(job);
        var before = _engine.FilesPulledTotal;

        Write(DeviceFolder("DeviceA"), "b.txt", "bravo-edited", mtimeOffsetSeconds: 30);
        await Run(job);

        Assert.Equal(before + 1, _engine.FilesPulledTotal);
        Assert.Equal("bravo-edited", File.ReadAllText(MasterFile("J2", "b.txt")));
        Assert.Equal("alpha", File.ReadAllText(MasterFile("J2", "a.txt")));
    }

    [Fact]
    public async Task DeviceSideDeletion_StillPropagatesToMaster()
    {
        var job = Job("J3", "DeviceA");
        Write(DeviceFolder("DeviceA"), "a.txt", "alpha");
        Write(DeviceFolder("DeviceA"), "b.txt", "bravo");
        await Run(job);

        File.Delete(Path.Combine(DeviceFolder("DeviceA"), "b.txt"));
        await Run(job);

        Assert.False(File.Exists(MasterFile("J3", "b.txt")));
        Assert.True(File.Exists(MasterFile("J3", "a.txt")));
    }

    [Fact]
    public async Task NewDeviceFile_IsPulledWhileOthersAreSkipped()
    {
        var job = Job("J4", "DeviceA");
        Write(DeviceFolder("DeviceA"), "a.txt", "alpha");
        await Run(job);
        var before = _engine.FilesPulledTotal;

        Write(DeviceFolder("DeviceA"), "c.txt", "charlie");
        await Run(job);

        Assert.Equal(before + 1, _engine.FilesPulledTotal);
        Assert.Equal("charlie", File.ReadAllText(MasterFile("J4", "c.txt")));
    }

    [Fact]
    public async Task TwoDevices_ChangeOnOnePropagatesToTheOtherWhileUnchangedFilesStayPut()
    {
        var job = Job("J5", "DeviceA", "DeviceB");
        Write(DeviceFolder("DeviceA"), "a.txt", "alpha");
        await Run(job);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(DeviceFolder("DeviceB"), "a.txt")));
        var before = _engine.FilesPulledTotal;

        Write(DeviceFolder("DeviceA"), "a.txt", "alpha-two", mtimeOffsetSeconds: 30);
        await Run(job);

        Assert.Equal("alpha-two", File.ReadAllText(MasterFile("J5", "a.txt")));
        Assert.Equal("alpha-two", File.ReadAllText(Path.Combine(DeviceFolder("DeviceB"), "a.txt")));
        // Device B re-pulls a.txt only if device A's merge reached master before B's master scan; both orders are safe.
        Assert.InRange(_engine.FilesPulledTotal, before + 1, before + 2);
    }

    [Fact]
    public async Task MasterSideChange_IsPulledForThatFileAndPushedToDevice()
    {
        var job = Job("J6", "DeviceA");
        Write(DeviceFolder("DeviceA"), "a.txt", "alpha");
        Write(DeviceFolder("DeviceA"), "b.txt", "bravo");
        await Run(job);

        Write(Path.Combine(_projectsDirectory, "J6", "master"), "a.txt", "alpha-from-master", mtimeOffsetSeconds: 30);
        await Run(job);

        Assert.Equal("alpha-from-master", File.ReadAllText(Path.Combine(DeviceFolder("DeviceA"), "a.txt")));
        Assert.Equal("bravo", File.ReadAllText(Path.Combine(DeviceFolder("DeviceA"), "b.txt")));
    }

    [Fact]
    public async Task MasterSideDeletion_PropagatesToDevice()
    {
        var job = Job("J7", "DeviceA");
        Write(DeviceFolder("DeviceA"), "a.txt", "alpha");
        Write(DeviceFolder("DeviceA"), "b.txt", "bravo");
        await Run(job);

        File.Delete(MasterFile("J7", "b.txt"));
        await Run(job);

        Assert.False(File.Exists(Path.Combine(DeviceFolder("DeviceA"), "b.txt")));
        Assert.True(File.Exists(Path.Combine(DeviceFolder("DeviceA"), "a.txt")));
    }

    [Fact]
    public async Task SettingOff_AlwaysPullsEverything()
    {
        _settings.IncrementalPull = false;
        var job = Job("J8", "DeviceA");
        Write(DeviceFolder("DeviceA"), "a.txt", "alpha");
        await Run(job);
        await Run(job);

        Assert.Equal(2, _engine.FilesPulledTotal);
    }

    [Fact]
    public async Task JobOverride_BeatsGlobalSetting()
    {
        var job = Job("J9", "DeviceA");
        job.IncrementalPull = false;
        Write(DeviceFolder("DeviceA"), "a.txt", "alpha");
        await Run(job);
        await Run(job);

        Assert.Equal(2, _engine.FilesPulledTotal);
    }

    [Fact]
    public async Task IntervalZero_AlwaysPullsEverything()
    {
        _settings.FullPullIntervalDays = 0;
        var job = Job("J10", "DeviceA");
        Write(DeviceFolder("DeviceA"), "a.txt", "alpha");
        await Run(job);
        await Run(job);

        Assert.Equal(2, _engine.FilesPulledTotal);
    }

    [Fact]
    public async Task StaleFullPullStamp_ForcesAFullPullAndRestampsIt()
    {
        var job = Job("J11", "DeviceA");
        Write(DeviceFolder("DeviceA"), "a.txt", "alpha");
        await Run(job);

        var store = new ManifestStore(_appPaths);
        var manifest = (await store.TryGetAsync("J11", "DeviceA"))!;
        Assert.NotNull(manifest.LastFullPullAtUtc);
        manifest.LastFullPullAtUtc = DateTimeOffset.UtcNow.AddDays(-30);
        await store.SaveAsync("J11", "DeviceA", manifest);

        await Run(job);
        Assert.Equal(2, _engine.FilesPulledTotal);

        var refreshed = (await store.TryGetAsync("J11", "DeviceA"))!;
        Assert.True(DateTimeOffset.UtcNow - refreshed.LastFullPullAtUtc < TimeSpan.FromMinutes(5));

        await Run(job);
        Assert.Equal(2, _engine.FilesPulledTotal);
    }

    [Fact]
    public async Task IncrementalRun_KeepsTheFullPullStampUnchanged()
    {
        var job = Job("J12", "DeviceA");
        Write(DeviceFolder("DeviceA"), "a.txt", "alpha");
        await Run(job);
        var store = new ManifestStore(_appPaths);
        var stamp = (await store.TryGetAsync("J12", "DeviceA"))!.LastFullPullAtUtc;

        await Run(job);

        Assert.Equal(stamp, (await store.TryGetAsync("J12", "DeviceA"))!.LastFullPullAtUtc);
    }

    [Fact]
    public async Task DryRun_AlwaysPullsEverything()
    {
        var job = Job("J13", "DeviceA");
        Write(DeviceFolder("DeviceA"), "a.txt", "alpha");
        await Run(job);
        var before = _engine.FilesPulledTotal;

        await Run(job, dryRun: true);

        Assert.Equal(before + 1, _engine.FilesPulledTotal);
    }

    [Fact]
    public async Task ClearedManifest_FallsBackToFullPull()
    {
        var job = Job("J14", "DeviceA");
        Write(DeviceFolder("DeviceA"), "a.txt", "alpha");
        await Run(job);
        await new ManifestStore(_appPaths).ClearAsync("J14");

        await Run(job);

        Assert.Equal(2, _engine.FilesPulledTotal);
        Assert.Equal("alpha", File.ReadAllText(MasterFile("J14", "a.txt")));
    }
}
