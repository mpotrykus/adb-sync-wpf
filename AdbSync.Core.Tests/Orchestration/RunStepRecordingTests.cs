using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Orchestration.RunHistory;
using AdbSync.Core.Tests.Orchestration.Fakes;

namespace AdbSync.Core.Tests.Orchestration;

public class RunStepRecordingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _appPaths;
    private readonly GlobalSettings _settings;

    public RunStepRecordingTests()
    {
        _appPaths = new AppPaths(Path.Combine(_root, "appdata"));
        _settings = new GlobalSettings { ProjectsDirectory = Path.Combine(_root, "projects"), StaleLockHours = 4 };
        ProjectFolders.AdoptRoot(_settings.ProjectsDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string DeviceFolder(string name) => Path.Combine(_root, "devices", name);

    private void WriteDeviceFile(string device, string name, string content)
    {
        Directory.CreateDirectory(DeviceFolder(device));
        File.WriteAllText(Path.Combine(DeviceFolder(device), name), content);
    }

    private SyncJobRunner CreateRunner(RunHistoryStore store, ILiveRunStepSink? liveSteps, params string[] devices) =>
        new(
            new FakeDeviceResolver(),
            new FakeAppRunningGuard(),
            new SyncLockManager(),
            new FakeAdbTransferEngine(devices.ToDictionary(d => d, DeviceFolder)),
            new TwoWayMergeEngine(),
            new ManifestStore(_appPaths),
            new PushSafetyGuard(_appPaths),
            new CheckpointManager(_appPaths),
            NullSyncEventSink.Instance,
            store,
            liveSteps: liveSteps);

    [Fact]
    public async Task RunAsync_TwoDevicesWithChanges_PersistsEveryDevicePhaseStep()
    {
        WriteDeviceFile("DeviceA", "a.txt", "from-a");
        WriteDeviceFile("DeviceB", "b.txt", "from-b");
        List<DeviceConfig> devices = [new() { Name = "DeviceA", Serial = "DeviceA" }, new() { Name = "DeviceB", Serial = "DeviceB" }];
        var job = new SyncJobConfig
        {
            Name = "Steps",
            Devices =
            [
                new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" },
                new JobDeviceBinding { DeviceName = "DeviceB", RemotePath = "/sdcard/app" },
            ],
        };
        var store = new RunHistoryStore(_appPaths);
        var liveSteps = new LiveRunStepSink();
        var runner = CreateRunner(store, liveSteps, "DeviceA", "DeviceB");

        var result = await runner.RunAsync(job, 0, devices, _settings, resumeFrom: null);

        Assert.True(result.Outcome == JobRunOutcome.Completed, $"{result.Outcome}: {result.ErrorMessage}");
        var record = Assert.Single(await store.ListRunsAsync(job.Name));
        var steps = await store.GetRunStepsAsync(record.RunId);

        Assert.Equal(8, steps.Count);
        Assert.All(steps, s => Assert.Equal(RunStepStatus.Succeeded, s.Status));
        Assert.All(steps, s => Assert.NotNull(s.StartedAt));
        Assert.All(steps, s => Assert.NotNull(s.EndedAt));
        Assert.Equal(1, steps.Single(s => s.DeviceName == "DeviceA" && s.Phase == SyncPhase.Pull).FilesCopied);
        Assert.False(liveSteps.TryGet(job.Name, out _));
    }

    [Fact]
    public async Task RunAsync_DryRun_MarksPushStepsSkipped()
    {
        WriteDeviceFile("DeviceA", "a.txt", "x");
        List<DeviceConfig> devices = [new() { Name = "DeviceA", Serial = "DeviceA" }];
        var job = new SyncJobConfig
        {
            Name = "NoPush",
            Devices = [new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" }],
        };
        var store = new RunHistoryStore(_appPaths);
        var runner = CreateRunner(store, null, "DeviceA");

        await runner.RunAsync(job, 0, devices, _settings, resumeFrom: null, dryRun: true);

        var latest = Assert.Single(await store.ListRunsAsync(job.Name));
        Assert.Equal(JobRunOutcome.DryRunCompleted, latest.Outcome);
        var steps = await store.GetRunStepsAsync(latest.RunId);
        Assert.Equal(RunStepStatus.Skipped, steps.Single(s => s.Phase == SyncPhase.Push).Status);
        Assert.Equal(RunStepStatus.Succeeded, steps.Single(s => s.Phase == SyncPhase.Pull).Status);
    }

    [Fact]
    public async Task RunAsync_TwoDevicesWithChanges_RecordsMergedFilesWithDirection()
    {
        WriteDeviceFile("DeviceA", "a.txt", "from-a");
        WriteDeviceFile("DeviceB", "b.txt", "from-b");
        List<DeviceConfig> devices = [new() { Name = "DeviceA", Serial = "DeviceA" }, new() { Name = "DeviceB", Serial = "DeviceB" }];
        var job = new SyncJobConfig
        {
            Name = "Files",
            Devices =
            [
                new JobDeviceBinding { DeviceName = "DeviceA", RemotePath = "/sdcard/app" },
                new JobDeviceBinding { DeviceName = "DeviceB", RemotePath = "/sdcard/app" },
            ],
        };
        var store = new RunHistoryStore(_appPaths);
        var runner = CreateRunner(store, null, "DeviceA", "DeviceB");

        var result = await runner.RunAsync(job, 0, devices, _settings, resumeFrom: null);
        Assert.True(result.Outcome == JobRunOutcome.Completed, $"{result.Outcome}: {result.ErrorMessage}");

        var record = Assert.Single(await store.ListRunsAsync(job.Name));
        var files = await store.GetRunFilesAsync(record.RunId);

        Assert.Contains(files, f => f is { DeviceName: "DeviceA", Phase: SyncPhase.Merge, Path: "a.txt", Action: RunFileAction.Created, Detail: "device → master" });
        Assert.Contains(files, f => f is { DeviceName: "DeviceB", Phase: SyncPhase.Merge, Path: "b.txt", Action: RunFileAction.Created, Detail: "device → master" });
        Assert.Contains(files, f => f is { Phase: SyncPhase.Push, Path: "a.txt" } && f.DeviceName == "DeviceB");
    }

    private static JobRunRecord Run(string job, DateTimeOffset startedAt) => new(
        Guid.NewGuid(), job, startedAt, startedAt.AddSeconds(1), JobRunOutcome.Completed, null, 1, 0, 0, 1, null, null);

    private static RunFileChange[] OneFile(string path) => [new RunFileChange("D", SyncPhase.Pull, path, RunFileAction.Copied)];

    [Fact]
    public async Task SaveRunAsync_KeepFileDetailRuns_PrunesFilesFromOlderRunsOnly()
    {
        var store = new RunHistoryStore(_appPaths);
        var start = DateTimeOffset.UtcNow;
        var runs = Enumerable.Range(0, 4).Select(i => Run("Job", start.AddMinutes(i))).ToList();
        foreach (var run in runs)
            await store.SaveRunAsync(run, "log", 50, null, OneFile($"{run.RunId:N}.txt"), keepFileDetailRuns: 2);

        Assert.Empty(await store.GetRunFilesAsync(runs[0].RunId));
        Assert.Empty(await store.GetRunFilesAsync(runs[1].RunId));
        Assert.Single(await store.GetRunFilesAsync(runs[2].RunId));
        Assert.Single(await store.GetRunFilesAsync(runs[3].RunId));
        Assert.Equal(4, (await store.ListRunsAsync("Job")).Count);
    }

    [Fact]
    public async Task SaveRunAsync_ZeroKeepFileDetailRuns_KeepsEveryRunsFiles()
    {
        var store = new RunHistoryStore(_appPaths);
        var start = DateTimeOffset.UtcNow;
        var runs = Enumerable.Range(0, 4).Select(i => Run("Job", start.AddMinutes(i))).ToList();
        foreach (var run in runs)
            await store.SaveRunAsync(run, "log", 50, null, OneFile("x.txt"), keepFileDetailRuns: 0);

        foreach (var run in runs)
            Assert.Single(await store.GetRunFilesAsync(run.RunId));
    }

    [Fact]
    public async Task SaveRunAsync_KeepFileDetailRuns_IsPerJob()
    {
        var store = new RunHistoryStore(_appPaths);
        var start = DateTimeOffset.UtcNow;
        var other = Run("Other", start);
        await store.SaveRunAsync(other, "log", 50, null, OneFile("o.txt"), keepFileDetailRuns: 1);
        for (var i = 1; i <= 3; i++)
            await store.SaveRunAsync(Run("Job", start.AddMinutes(i)), "log", 50, null, OneFile("j.txt"), keepFileDetailRuns: 1);

        Assert.Single(await store.GetRunFilesAsync(other.RunId));
    }

    [Fact]
    public async Task SaveRunAsync_WithoutSteps_ReturnsNoSteps()
    {
        var store = new RunHistoryStore(_appPaths);
        var record = new JobRunRecord(Guid.NewGuid(), "Old", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, JobRunOutcome.Completed, null, 0, 0, 0, 0, null, null);
        await store.SaveRunAsync(record, "log", 10);

        Assert.Empty(await store.GetRunStepsAsync(record.RunId));
    }
}
