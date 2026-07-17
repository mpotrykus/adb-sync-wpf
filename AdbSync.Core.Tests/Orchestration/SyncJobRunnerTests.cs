using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Orchestration.RunHistory;
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
    public async Task RunAsync_OneDeviceHeldByAnotherCaller_OtherDeviceStillPullsWithoutWaiting()
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
            Name = "JobConcurrentDevices",
            Devices =
            [
                new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" },
                new JobDeviceBinding { DeviceName = "DeviceB", RemotePath = "/sdcard/app" },
            ],
        };

        var gate = new DeviceAccessGate();
        var externalDeviceALock = await gate.AcquireAsync("DeviceA", 1);
        var deviceBPulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new DeviceObservingSyncEventSink((phase, deviceName) =>
        {
            if (phase == SyncPhase.Pull && deviceName == "DeviceB")
                deviceBPulled.TrySetResult();
        });

        var runner = new SyncJobRunner(
            new FakeDeviceResolver(),
            new FakeAppRunningGuard(),
            new SyncLockManager(),
            new FakeAdbTransferEngine(new Dictionary<string, string>
            {
                ["DeviceA"] = DeviceFolder("DeviceA"),
                ["DeviceB"] = DeviceFolder("DeviceB"),
            }),
            new TwoWayMergeEngine(),
            new ManifestStore(_appPaths),
            new PushSafetyGuard(_appPaths),
            new CheckpointManager(_appPaths),
            sink,
            new RunHistoryStore(_appPaths),
            deviceAccessGate: gate);

        var runTask = runner.RunAsync(job, 0, devices, _settings, resumeFrom: null);

        var won = await Task.WhenAny(deviceBPulled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(deviceBPulled.Task, won);

        await externalDeviceALock.DisposeAsync();
        var result = await runTask;

        Assert.Equal(JobRunOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task RunAsync_DeviceHeldByAnotherCaller_ReportsWaitingForDeviceBeforeItsOwnPullPhase()
    {
        WriteDeviceFile("DeviceA", "a.txt", "from-a");
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobWaitsOnDevice",
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };

        var gate = new DeviceAccessGate();
        var externalDeviceALock = await gate.AcquireAsync("DeviceA", 1);
        var reportedWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new DeviceObservingSyncEventSink((phase, deviceName) =>
        {
            if (phase == SyncPhase.WaitingForDevice && deviceName == "DeviceA")
                reportedWaiting.TrySetResult();
        });

        var runner = new SyncJobRunner(
            new FakeDeviceResolver(),
            new FakeAppRunningGuard(),
            new SyncLockManager(),
            new FakeAdbTransferEngine(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") }),
            new TwoWayMergeEngine(),
            new ManifestStore(_appPaths),
            new PushSafetyGuard(_appPaths),
            new CheckpointManager(_appPaths),
            sink,
            new RunHistoryStore(_appPaths),
            deviceAccessGate: gate);

        var runTask = runner.RunAsync(job, 0, [device], _settings, resumeFrom: null);

        var won = await Task.WhenAny(reportedWaiting.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(reportedWaiting.Task, won);

        await externalDeviceALock.DisposeAsync();
        var result = await runTask;

        Assert.Equal(JobRunOutcome.CompletedNoChanges, result.Outcome);
    }

    [Fact]
    public async Task RunAsync_ResumedPullWithOneDeviceAlreadyCompleted_SkipsThatDeviceButStillPushesToIt()
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
            Name = "JobResumeSkip",
            Devices =
            [
                new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" },
                new JobDeviceBinding { DeviceName = "DeviceB", RemotePath = "/sdcard/app" },
            ],
        };
        var pullPhaseDevices = new List<string>();
        var sink = new DeviceObservingSyncEventSink((phase, deviceName) =>
        {
            if (phase == SyncPhase.Pull && deviceName is not null)
                pullPhaseDevices.Add(deviceName);
        });
        var runner = new SyncJobRunner(
            new FakeDeviceResolver(),
            new FakeAppRunningGuard(),
            new SyncLockManager(),
            new FakeAdbTransferEngine(new Dictionary<string, string>
            {
                ["DeviceA"] = DeviceFolder("DeviceA"),
                ["DeviceB"] = DeviceFolder("DeviceB"),
            }),
            new TwoWayMergeEngine(),
            new ManifestStore(_appPaths),
            new PushSafetyGuard(_appPaths),
            new CheckpointManager(_appPaths),
            sink,
            new RunHistoryStore(_appPaths));

        var resumeFrom = new SyncCheckpoint(1, DateTimeOffset.UtcNow, 0, job.Name, SyncPhase.Pull, ["DeviceA"],
            new Dictionary<string, string> { ["DeviceA"] = "DeviceA", ["DeviceB"] = "DeviceB" });

        var result = await runner.RunAsync(job, 0, devices, _settings, resumeFrom);

        Assert.Equal(JobRunOutcome.Completed, result.Outcome);
        Assert.DoesNotContain("DeviceA", pullPhaseDevices);
        Assert.Contains("DeviceB", pullPhaseDevices);
        Assert.Equal("from-b", File.ReadAllText(Path.Combine(DeviceFolder("DeviceA"), "b.txt")));
    }

    private sealed class DeviceObservingSyncEventSink(Action<SyncPhase, string?> onPhaseChanged) : ISyncEventSink
    {
        public void PhaseChanged(string jobName, SyncPhase phase, string? deviceName = null) => onPhaseChanged(phase, deviceName);
        public void JobQueued(string jobName, string reason) { }
        public void JobSkipped(string jobName, string reason) { }
        public void JobCompleted(string jobName, bool pushed) { }
        public void JobFailed(string jobName, Exception exception) { }
        public void JobCancelled(string jobName) { }
        public void MergeConflictsDetected(string jobName, string deviceName, int conflictCount) { }
        public void WatchStarted(string jobName, string deviceName, bool liveWatch) { }
        public void WatchDegraded(string jobName, string deviceName, string reason) { }
        public void WatchStopped(string jobName, string deviceName) { }
        public void ChangeDetected(string jobName, string deviceName) { }
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

        await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null);

        var resumeFrom = new SyncCheckpoint(1, DateTimeOffset.UtcNow, 0, job.Name, SyncPhase.Push, [], new Dictionary<string, string> { ["DeviceA"] = "DeviceA" });
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
        await pushSafety.RecordDeviceSnapshotAsync("JobSix", "DeviceA", 100);

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
        await pushSafety.RecordDeviceSnapshotAsync("JobSeven", "DeviceA", 100);

        WriteDeviceFile("DeviceA", "only-one-file.txt", "content");
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobSeven",
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var runner = CreateRunner(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") }, pushSafety: pushSafety);

        var result = await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null, forcePush: true);
        Assert.Equal(JobRunOutcome.CompletedNoChanges, result.Outcome);

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

        Assert.Null(await checkpoints.LoadAsync(job.Name));
    }

    [Fact]
    public async Task RunAsync_IndependentConflictingCreate_ReportsConflictViaEventSink()
    {
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

        Assert.Equal(JobRunOutcome.CompletedNoChanges, result.Outcome);
        Assert.Equal(1, recordingSink.TotalConflictsReported);
        Assert.Equal("device-version-newer", File.ReadAllText(Path.Combine(masterPath, "a.txt")));
    }

    [Fact]
    public async Task RunAsync_ConflictBackup_SurvivesStagingCleanupAndDoesNotLeakIntoMaster()
    {
        var projectRoot = Path.Combine(_projectsDirectory, "JobConflictBackup");
        var masterPath = Path.Combine(projectRoot, "master");
        Directory.CreateDirectory(masterPath);
        File.WriteAllText(Path.Combine(masterPath, "a.txt"), "master-version");
        File.SetLastWriteTimeUtc(Path.Combine(masterPath, "a.txt"), DateTime.UtcNow.AddMinutes(-10));

        WriteDeviceFile("DeviceA", "a.txt", "device-version-newer");
        File.SetLastWriteTimeUtc(Path.Combine(DeviceFolder("DeviceA"), "a.txt"), DateTime.UtcNow);

        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobConflictBackup",
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var runner = CreateRunner(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") });

        await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null);

        var backupDir = Path.Combine(projectRoot, ".sync_conflicts", "DeviceA");
        Assert.True(Directory.Exists(backupDir) && Directory.EnumerateFiles(backupDir).Any(),
            "Expected a conflict backup to survive under the project root.");

        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(masterPath, "*", SearchOption.AllDirectories),
            p => p.Contains(".conflicts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_PrunesConflictBackupsOlderThanRetention()
    {
        var projectRoot = Path.Combine(_projectsDirectory, "JobPrune");
        var masterPath = Path.Combine(projectRoot, "master");
        Directory.CreateDirectory(masterPath);

        var backupDir = Path.Combine(projectRoot, ".sync_conflicts", "DeviceA");
        Directory.CreateDirectory(backupDir);
        var stalePath = Path.Combine(backupDir, "stale.txt.conflict");
        File.WriteAllText(stalePath, "stale-backup");
        File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddDays(-40));
        var freshPath = Path.Combine(backupDir, "fresh.txt.conflict");
        File.WriteAllText(freshPath, "fresh-backup");
        File.SetLastWriteTimeUtc(freshPath, DateTime.UtcNow.AddDays(-1));

        WriteDeviceFile("DeviceA", "photo.jpg", "content");
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobPrune",
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        _settings.ConflictRetentionDays = 30;
        var runner = CreateRunner(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") });

        await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null);

        Assert.False(File.Exists(stalePath), "Expected the 40-day-old backup to be pruned.");
        Assert.True(File.Exists(freshPath), "Expected the 1-day-old backup to survive.");
    }

    [Fact]
    public async Task RunAsync_DryRun_NeverWritesMasterOrPushesAndReportsDryRunCompleted()
    {
        WriteDeviceFile("DeviceA", "photo.jpg", "content");
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobDryRun",
            DryRun = true,
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

        var result = await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null);

        Assert.Equal(JobRunOutcome.DryRunCompleted, result.Outcome);
        var masterPath = Path.Combine(_projectsDirectory, "JobDryRun", "master");
        Assert.Empty(Directory.EnumerateFiles(masterPath, "*", SearchOption.AllDirectories));
        Assert.Null(await checkpoints.LoadAsync(job.Name));
    }

    [Fact]
    public async Task RunAsync_PushSafetyMinimumPercentOverride_UsesJobValueInsteadOfGlobalDefault()
    {
        var pushSafety = new PushSafetyGuard(_appPaths);
        await pushSafety.RecordDeviceSnapshotAsync("JobPercentOverride", "DeviceA", 100);

        WriteDeviceFile("DeviceA", "one.txt", "content");
        for (var i = 1; i < 24; i++)
            WriteDeviceFile("DeviceA", $"file{i}.txt", "content");
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobPercentOverride",
            PushSafetyMinimumPercent = 20,
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var runner = CreateRunner(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") }, pushSafety: pushSafety);

        var result = await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null);

        Assert.Equal(JobRunOutcome.CompletedNoChanges, result.Outcome);
    }

    [Fact]
    public async Task RunAsync_PopulatesLiveLogSinkWhileRunningAndClearsItOnceComplete()
    {
        WriteDeviceFile("DeviceA", "a.txt", "hello");
        var device = new DeviceConfig { Name = "DeviceA", Serial = "DeviceA" };
        var job = new SyncJobConfig
        {
            Name = "JobLiveLog",
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var liveLog = new LiveRunLogSink();
        var wasLiveDuringRun = false;
        var sink = new PhaseCapturingSyncEventSink(() => wasLiveDuringRun |= liveLog.TryGet(job.Name, out _, out _));
        var runner = new SyncJobRunner(
            new FakeDeviceResolver(),
            new FakeAppRunningGuard(),
            new SyncLockManager(),
            new FakeAdbTransferEngine(new Dictionary<string, string> { ["DeviceA"] = DeviceFolder("DeviceA") }),
            new TwoWayMergeEngine(),
            new ManifestStore(_appPaths),
            new PushSafetyGuard(_appPaths),
            new CheckpointManager(_appPaths),
            sink,
            new RunHistoryStore(_appPaths),
            liveLog: liveLog);

        await runner.RunAsync(job, 0, [device], _settings, resumeFrom: null);

        Assert.True(wasLiveDuringRun);
        Assert.False(liveLog.TryGet(job.Name, out _, out _));
    }

    private sealed class PhaseCapturingSyncEventSink(Action onPhaseChanged) : ISyncEventSink
    {
        public void PhaseChanged(string jobName, SyncPhase phase, string? deviceName = null) => onPhaseChanged();
        public void JobQueued(string jobName, string reason) { }
        public void JobSkipped(string jobName, string reason) { }
        public void JobCompleted(string jobName, bool pushed) { }
        public void JobFailed(string jobName, Exception exception) { }
        public void JobCancelled(string jobName) { }
        public void MergeConflictsDetected(string jobName, string deviceName, int conflictCount) { }
        public void WatchStarted(string jobName, string deviceName, bool liveWatch) { }
        public void WatchDegraded(string jobName, string deviceName, string reason) { }
        public void WatchStopped(string jobName, string deviceName) { }
        public void ChangeDetected(string jobName, string deviceName) { }
    }
}
