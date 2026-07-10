using AdbSync.Core.Config;
using AdbSync.Core.Merge;
using AdbSync.Core.Orchestration;
using AdbSync.Core.Orchestration.RunHistory;
using AdbSync.Core.Tests.Orchestration.Fakes;

namespace AdbSync.Core.Tests.Orchestration;

/// <summary>
/// End-to-end orchestration tests: only the device-touching interfaces (resolver, app guard, transfer) are
/// faked - locking, checkpointing, manifests, push-safety, and the merge engine are all the real implementations
/// running against temp directories, so this exercises the genuine pipeline wiring, not just each piece in isolation.
/// </summary>
public class SyncJobRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _projectsDirectory;
    private readonly AppPaths _appPaths;
    private readonly GlobalSettings _settings;

    public SyncJobRunnerTests()
    {
        _projectsDirectory = Path.Combine(_root, "projects");
        _appPaths = new AppPaths(Path.Combine(_root, "appdata"));
        _settings = new GlobalSettings { ProjectsDirectory = _projectsDirectory, StaleLockHours = 4 };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string DeviceFolder(string name) => Path.Combine(_root, "devices", name);

    private SyncJobRunner CreateRunner(
        IReadOnlyDictionary<string, string> deviceFolders, bool appRunning = false, IPushSafetyGuard? pushSafety = null, ISyncEventSink? events = null) =>
        new(
            new FakeDeviceResolver(),
            new FakeAppRunningGuard(appRunning),
            new SyncLockManager(),
            new FakeAdbTransferEngine(deviceFolders),
            new TwoWayMergeEngine(),
            new ManifestStore(_appPaths),
            pushSafety ?? new PushSafetyGuard(_appPaths),
            new CheckpointManager(_appPaths),
            events ?? NullSyncEventSink.Instance,
            new RunHistoryStore(_appPaths));

    private void WriteDeviceFile(string deviceName, string relativePath, string content)
    {
        var path = Path.Combine(DeviceFolder(deviceName), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task RunAsync_SingleDeviceWithNewFile_MergesIntoMasterWithNoChangesToPush()
    {
        WriteDeviceFile("DeviceA", "photo.jpg", "content");
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobOne",
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var runner = CreateRunner(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") });

        var result = await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null);

        // Outcome reflects the push phase only: DeviceA already has photo.jpg (it's the file's origin), so
        // pushing back to it copies nothing even though the pull did populate the master mirror.
        Assert.Equal(JobRunOutcome.CompletedNoChanges, result.Outcome);
        var masterPath = Path.Combine(_projectsDirectory, "JobOne", "master");
        Assert.Equal("content", File.ReadAllText(Path.Combine(masterPath, "photo.jpg")));
    }

    [Fact]
    public async Task RunAsync_TwoDevicesWithDifferentFiles_BothEndUpWithBothFiles()
    {
        WriteDeviceFile("DeviceA", "a.txt", "from-a");
        WriteDeviceFile("DeviceB", "b.txt", "from-b");
        List<DeviceConfig> devices =
        [
            new() { Name = "DeviceA", Serial = "DeviceA" },
            new() { Name = "DeviceB", Serial = "DeviceB" },
        ];
        var job = new SyncJobConfig
        {
            Name = "JobTwo",
            Devices =
            [
                new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" },
                new JobDeviceBinding { DeviceName = "DeviceB", RemotePath = "/sdcard/app" },
            ],
        };
        var runner = CreateRunner(new Dictionary<string, string>
        {
            ["DeviceA"] = DeviceFolder("DeviceA"),
            ["DeviceB"] = DeviceFolder("DeviceB"),
        });

        var result = await runner.RunAsync(job, 0, devices, _settings, resumeFrom: null);

        Assert.Equal(JobRunOutcome.Completed, result.Outcome);
        Assert.Equal("from-b", File.ReadAllText(Path.Combine(DeviceFolder("DeviceA"), "b.txt")));
        Assert.Equal("from-a", File.ReadAllText(Path.Combine(DeviceFolder("DeviceB"), "a.txt")));
    }

    [Fact]
    public async Task RunAsync_FileFromOneDevicePushedToTwoOtherDevices_ReportsUniqueFileCountNotPerDeviceSum()
    {
        WriteDeviceFile("DeviceA", "photo.jpg", "content");
        List<DeviceConfig> devices =
        [
            new() { Name = "DeviceA", Serial = "DeviceA" },
            new() { Name = "DeviceB", Serial = "DeviceB" },
            new() { Name = "DeviceC", Serial = "DeviceC" },
        ];
        var job = new SyncJobConfig
        {
            Name = "JobUniqueFiles",
            Devices =
            [
                new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" },
                new JobDeviceBinding { DeviceName = "DeviceB", RemotePath = "/sdcard/app" },
                new JobDeviceBinding { DeviceName = "DeviceC", RemotePath = "/sdcard/app" },
            ],
        };
        var historyStore = new RunHistoryStore(_appPaths);
        var runner = new SyncJobRunner(
            new FakeDeviceResolver(),
            new FakeAppRunningGuard(),
            new SyncLockManager(),
            new FakeAdbTransferEngine(new Dictionary<string, string>
            {
                ["DeviceA"] = DeviceFolder("DeviceA"),
                ["DeviceB"] = DeviceFolder("DeviceB"),
                ["DeviceC"] = DeviceFolder("DeviceC"),
            }),
            new TwoWayMergeEngine(),
            new ManifestStore(_appPaths),
            new PushSafetyGuard(_appPaths),
            new CheckpointManager(_appPaths),
            NullSyncEventSink.Instance,
            historyStore);

        var result = await runner.RunAsync(job, 0, devices, _settings, resumeFrom: null);

        Assert.Equal(JobRunOutcome.Completed, result.Outcome);
        Assert.Equal("content", File.ReadAllText(Path.Combine(DeviceFolder("DeviceB"), "photo.jpg")));
        Assert.Equal("content", File.ReadAllText(Path.Combine(DeviceFolder("DeviceC"), "photo.jpg")));

        var runs = await historyStore.ListRunsAsync(job.Name);
        var record = Assert.Single(runs);
        Assert.Equal(1, record.FilesCopied);
    }

    [Fact]
    public async Task RunAsync_ResumedIntoPushPhaseWithNothingLeftToPush_ReturnsCompletedNoChanges()
    {
        WriteDeviceFile("DeviceA", "photo.jpg", "content");
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobResume",
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var runner = CreateRunner(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") });

        // Establish a baseline where master and the device already agree.
        await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null);

        // Simulate resuming a crashed run that jumps straight into the push phase - since nothing
        // changed since the baseline, push has nothing left to do and the run should read as a no-op.
        var resumeFrom = new SyncCheckpoint(1, DateTimeOffset.UtcNow, 0, job.Name, SyncPhase.Push, 0, new Dictionary<string, string> { ["DeviceA"] = "DeviceA" });
        var result = await runner.RunAsync(job, 0, [device], _settings, resumeFrom);

        Assert.Equal(JobRunOutcome.CompletedNoChanges, result.Outcome);
    }

    [Fact]
    public async Task RunAsync_NothingChanged_ReturnsCompletedNoChanges()
    {
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobThree",
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var runner = CreateRunner(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") });

        var result = await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null);

        Assert.Equal(JobRunOutcome.CompletedNoChanges, result.Outcome);
    }

    [Fact]
    public async Task RunAsync_AppRunningOnDevice_SkipsWithoutTouchingFiles()
    {
        WriteDeviceFile("DeviceA", "a.txt", "content");
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobFour",
            AppPackage = "com.example.app",
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var runner = CreateRunner(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") }, appRunning: true);

        var result = await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null);

        Assert.Equal(JobRunOutcome.SkippedAppRunning, result.Outcome);
        var masterPath = Path.Combine(_projectsDirectory, "JobFour", "master");
        Assert.False(File.Exists(Path.Combine(masterPath, "a.txt")));
    }

    [Fact]
    public async Task RunAsync_LockAlreadyHeldByLiveProcess_ReturnsSkipped()
    {
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobFive",
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var projectRoot = Path.Combine(_projectsDirectory, "JobFive");
        var lockPath = SyncLockManager.GetLockPath(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        File.WriteAllText(lockPath, $"pid={Environment.ProcessId}\nstart={DateTimeOffset.UtcNow:o}");

        var runner = CreateRunner(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") });
        var result = await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null);

        Assert.Equal(JobRunOutcome.Skipped, result.Outcome);
    }

    [Fact]
    public async Task RunAsync_PushSafetyTripped_ReturnsFailedWithoutThrowing()
    {
        var pushSafety = new PushSafetyGuard(_appPaths);
        await pushSafety.RecordDeviceSnapshotAsync("JobSix", "DeviceA", 100); // seed a much higher historical baseline

        WriteDeviceFile("DeviceA", "only-one-file.txt", "content");
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobSix",
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var runner = CreateRunner(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") }, pushSafety: pushSafety);

        var result = await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null);

        Assert.Equal(JobRunOutcome.Failed, result.Outcome);
        Assert.Contains("Safety check blocked", result.ErrorMessage);
    }

    [Fact]
    public async Task RunAsync_ForcePushWithSafetyTripped_CompletesAndRebasesBaseline()
    {
        var pushSafety = new PushSafetyGuard(_appPaths);
        await pushSafety.RecordDeviceSnapshotAsync("JobSeven", "DeviceA", 100); // seed a much higher historical baseline

        WriteDeviceFile("DeviceA", "only-one-file.txt", "content");
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobSeven",
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var runner = CreateRunner(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") }, pushSafety: pushSafety);

        var result = await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null, forcePush: true);
        // Both runs push back to the very same device the content came from, so nothing is actually copied out -
        // the push-phase file count is what drives the outcome, so these correctly read as no-op pushes.
        Assert.Equal(JobRunOutcome.CompletedNoChanges, result.Outcome);

        // Baseline is now rebased to the 1-file master, so a normal (non-forced) run at the same level no longer trips the guard.
        WriteDeviceFile("DeviceA", "only-one-file.txt", "content edited");
        var secondResult = await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null);
        Assert.Equal(JobRunOutcome.CompletedNoChanges, secondResult.Outcome);
    }

    [Fact]
    public async Task RunAsync_ProjectsDirectoryNotConfigured_ReturnsFailedAndFiresJobFailedEvent()
    {
        WriteDeviceFile("DeviceA", "a.txt", "content");
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobNine",
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var recordingSink = new RecordingSyncEventSink();
        var runner = new SyncJobRunner(
            new FakeDeviceResolver(),
            new FakeAppRunningGuard(),
            new SyncLockManager(),
            new FakeAdbTransferEngine(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") }),
            new TwoWayMergeEngine(),
            new ManifestStore(_appPaths),
            new PushSafetyGuard(_appPaths),
            new CheckpointManager(_appPaths),
            recordingSink,
            new RunHistoryStore(_appPaths));

        var settingsWithNoProjectsDirectory = new GlobalSettings { ProjectsDirectory = "" };
        var result = await runner.RunAsync(job, 0, [device], settingsWithNoProjectsDirectory, resumeFrom: null);

        Assert.Equal(JobRunOutcome.Failed, result.Outcome);
        Assert.NotNull(result.ErrorMessage);
        Assert.True(recordingSink.Failed, "ISyncEventSink.JobFailed should fire even for failures before the main pipeline starts.");
    }

    [Fact]
    public async Task RunAsync_UnknownDeviceReference_ReturnsFailedWithoutThrowing()
    {
        var job = new SyncJobConfig
        {
            Name = "JobSeven",
            Devices = [new JobDeviceBinding { DeviceName = "DoesNotExist", RemotePath = "/sdcard/app" }],
        };
        var runner = CreateRunner(new Dictionary<string, string>());

        var result = await runner.RunAsync(job, 0, [], _settings, resumeFrom: null);

        Assert.Equal(JobRunOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task RunAsync_OnSuccess_ClearsCheckpoint()
    {
        WriteDeviceFile("DeviceA", "a.txt", "content");
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobEight",
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var checkpoints = new CheckpointManager(_appPaths);
        var runner = new SyncJobRunner(
            new FakeDeviceResolver(),
            new FakeAppRunningGuard(),
            new SyncLockManager(),
            new FakeAdbTransferEngine(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") }),
            new TwoWayMergeEngine(),
            new ManifestStore(_appPaths),
            new PushSafetyGuard(_appPaths),
            checkpoints,
            NullSyncEventSink.Instance,
            new RunHistoryStore(_appPaths));

        await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null);

        Assert.Null(await checkpoints.LoadAsync());
    }

    [Fact]
    public async Task RunAsync_IndependentConflictingCreate_ReportsConflictViaEventSink()
    {
        // Master already has a file with no manifest baseline (e.g. from a previous device), and the device
        // independently has a *different* version of the same file - classic no-shared-history conflict.
        var masterPath = Path.Combine(_projectsDirectory, "JobConflict", "master");
        Directory.CreateDirectory(masterPath);
        File.WriteAllText(Path.Combine(masterPath, "a.txt"), "master-version");
        File.SetLastWriteTimeUtc(Path.Combine(masterPath, "a.txt"), DateTime.UtcNow.AddMinutes(-10));

        WriteDeviceFile("DeviceA", "a.txt", "device-version-newer");
        File.SetLastWriteTimeUtc(Path.Combine(DeviceFolder("DeviceA"), "a.txt"), DateTime.UtcNow);

        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobConflict",
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var recordingSink = new RecordingSyncEventSink();
        var runner = CreateRunner(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") }, events: recordingSink);

        var result = await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null);

        Assert.Equal(JobRunOutcome.Completed, result.Outcome);
        Assert.Equal(1, recordingSink.TotalConflictsReported);
        Assert.Equal("device-version-newer", File.ReadAllText(Path.Combine(masterPath, "a.txt")));
    }
}
