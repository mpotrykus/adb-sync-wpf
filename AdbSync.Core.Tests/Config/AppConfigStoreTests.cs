using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Persistence;

namespace AdbSync.Core.Tests.Config;

public class AppConfigStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsAllFields()
    {
        var store = new AppConfigStore(new AppPaths(_root));
        var original = new AppConfig
        {
            Settings = new GlobalSettings
            {
                ProjectsDirectory = @"C:\Backups\adb-sync",
                StartAtLogin = false,
                ShowInfoNotifications = true,
                MaxConcurrentJobs = 2,
            },
            Devices =
            [
                new DeviceConfig { Name = "S23+", Ip = "192.168.0.40" },
                new DeviceConfig { Name = "BlueStacks", Serial = "emulator-5554" },
            ],
            Jobs =
            [
                new SyncJobConfig
                {
                    Name = "NomadSculpt",
                    AppPackage = "com.stephaneginier.nomad",
                    Exclude = ["tmp_session", "data"],
                    Devices =
                    [
                        new JobDeviceBinding
                        {
                            DeviceName = "S23+",
                            RemotePath = "/storage/emulated/0/Android/data/com.stephaneginier.nomad/files",
                        },
                    ],
                    Schedule = new JobSchedule { Kind = ScheduleKind.Interval, Interval = TimeSpan.FromHours(6) },
                },
            ],
        };

        await store.SaveAsync(original);
        var loaded = await store.LoadAsync();

        Assert.Equal(original.Settings.ProjectsDirectory, loaded.Settings.ProjectsDirectory);
        Assert.False(loaded.Settings.StartAtLogin);
        Assert.True(loaded.Settings.ShowInfoNotifications);
        Assert.Equal(2, loaded.Settings.MaxConcurrentJobs);

        Assert.Equal(2, loaded.Devices.Count);
        Assert.Equal("192.168.0.40", loaded.Devices.Single(d => d.Name == "S23+").Ip);
        Assert.Equal("emulator-5554", loaded.Devices.Single(d => d.Name == "BlueStacks").Serial);

        var job = Assert.Single(loaded.Jobs);
        Assert.Equal("NomadSculpt", job.Name);
        Assert.Equal(["tmp_session", "data"], job.Exclude);
        Assert.Equal(ScheduleKind.Interval, job.Schedule.Kind);
        Assert.Equal(TimeSpan.FromHours(6), job.Schedule.Interval);
    }

    [Fact]
    public async Task LoadAsync_WhenNoFilesExist_ReturnsDefaults()
    {
        var store = new AppConfigStore(new AppPaths(_root));

        var loaded = await store.LoadAsync();

        Assert.Empty(loaded.Devices);
        Assert.Empty(loaded.Jobs);
        Assert.Equal(new AppPaths(_root).DefaultProjectsDirectory, loaded.Settings.ProjectsDirectory);
    }

    private async Task<(string Json, long Version)> ReadJobRowAsync(Guid id)
    {
        await using var connection = await new EngineDatabase(new AppPaths(_root)).OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json, Version FROM Jobs WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt64(1));
    }

    private static AppConfig OneDeviceOneJob() => new()
    {
        Settings = new GlobalSettings { ProjectsDirectory = Path.Combine(Path.GetTempPath(), "projects") },
        Devices = [new DeviceConfig { Name = "Pixel", Ip = "192.168.0.40" }],
        Jobs =
        [
            new SyncJobConfig
            {
                Name = "Photos",
                Devices = [new JobDeviceBinding { DeviceName = "Pixel", RemotePath = "/sdcard/DCIM" }],
            },
        ],
    };

    [Fact]
    public async Task LoadAsync_LegacyJsonFiles_AreImportedWithIdsAndMovedToTheBackupFolder()
    {
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(paths.ConfigDir);
        File.WriteAllText(paths.SettingsFile, """{ "projectsDirectory": "D:\\Projects", "maxConcurrentJobs": 3 }""");
        File.WriteAllText(paths.DevicesFile, """{ "devices": [ { "name": "Pixel", "ip": "192.168.0.40", "cachedHostPort": "192.168.0.40:5555" } ] }""");
        File.WriteAllText(paths.ProjectsFile, """
            { "projects": [ { "name": "Photos", "devices": [ { "deviceName": "Pixel", "remotePath": "/sdcard/DCIM" } ],
                              "schedule": { "kind": 3, "interval": "04:00:00", "lastRunAt": "2026-11-09T07:10:25.5670918-06:00" } } ] }
            """);

        var loaded = await new AppConfigStore(paths).LoadAsync();

        Assert.Equal(@"D:\Projects", loaded.Settings.ProjectsDirectory);
        Assert.Equal(3, loaded.Settings.MaxConcurrentJobs);
        var device = Assert.Single(loaded.Devices);
        var job = Assert.Single(loaded.Jobs);
        Assert.NotEqual(Guid.Empty, device.Id);
        Assert.NotEqual(Guid.Empty, job.Id);
        Assert.Equal(device.Id, Assert.Single(job.Devices).DeviceId);
        Assert.Equal("192.168.0.40:5555", device.CachedHostPort);
        Assert.Equal(DateTimeOffset.Parse("2026-11-09T07:10:25.5670918-06:00"), job.Schedule.LastRunAt);
        Assert.Equal(ScheduleKind.OnChange, job.Schedule.Kind);

        Assert.False(File.Exists(paths.ProjectsFile));
        Assert.True(File.Exists(Path.Combine(_root, EngineDatabase.BackupFolderName, "config", "projects.json")));
    }

    [Fact]
    public async Task SaveAsync_IdsStayStableAcrossSavesAndAReplacedJobObjectWithTheSameId()
    {
        var store = new AppConfigStore(new AppPaths(_root));
        var config = OneDeviceOneJob();
        await store.SaveAsync(config);
        var jobId = config.Jobs[0].Id;

        var loaded = await store.LoadAsync();
        loaded.Jobs[0] = new SyncJobConfig { Id = loaded.Jobs[0].Id, Name = "Photos", Devices = loaded.Jobs[0].Devices, AppPackage = "com.example" };
        await store.SaveAsync(loaded);

        var reloaded = await store.LoadAsync();
        Assert.Equal(jobId, Assert.Single(reloaded.Jobs).Id);
        Assert.Equal("com.example", reloaded.Jobs[0].AppPackage);
    }

    [Fact]
    public async Task SaveAsync_VersionOnlyChangesWhenTheDocumentDoes()
    {
        var store = new AppConfigStore(new AppPaths(_root));
        var config = OneDeviceOneJob();
        await store.SaveAsync(config);
        var id = config.Jobs[0].Id;
        Assert.Equal(1, (await ReadJobRowAsync(id)).Version);

        config.Jobs[0].Schedule.LastRunAt = DateTimeOffset.UtcNow;
        await store.SaveAsync(config);
        Assert.Equal(1, (await ReadJobRowAsync(id)).Version);

        config.Jobs[0].Enabled = false;
        await store.SaveAsync(config);
        Assert.Equal(2, (await ReadJobRowAsync(id)).Version);
    }

    [Fact]
    public async Task SaveAsync_RuntimeStateIsStoredOutsideTheConfigDocument()
    {
        var store = new AppConfigStore(new AppPaths(_root));
        var config = OneDeviceOneJob();
        var lastRun = new DateTimeOffset(2026, 11, 11, 9, 30, 0, TimeSpan.Zero);
        config.Jobs[0].Schedule.LastRunAt = lastRun;
        config.Devices[0].CachedHostPort = "192.168.0.40:41234";
        await store.SaveAsync(config);

        var (json, _) = await ReadJobRowAsync(config.Jobs[0].Id);
        Assert.DoesNotContain("lastRunAt", json);
        var loaded = await store.LoadAsync();
        Assert.Equal(lastRun, loaded.Jobs[0].Schedule.LastRunAt);
        Assert.Equal("192.168.0.40:41234", loaded.Devices[0].CachedHostPort);
    }

    [Fact]
    public async Task SaveAsync_NamesDifferingOnlyInCase_AreRejectedAndNothingIsSaved()
    {
        var store = new AppConfigStore(new AppPaths(_root));
        var config = OneDeviceOneJob();
        await store.SaveAsync(config);

        config.Jobs.Add(new SyncJobConfig { Name = "photos" });
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(config));

        Assert.Single((await store.LoadAsync()).Jobs);
    }

    [Fact]
    public async Task SaveAsync_RemovedJobIsDeletedWithItsRuntimeState()
    {
        var store = new AppConfigStore(new AppPaths(_root));
        var config = OneDeviceOneJob();
        config.Jobs[0].Schedule.LastRunAt = DateTimeOffset.UtcNow;
        await store.SaveAsync(config);

        config.Jobs.Clear();
        await store.SaveAsync(config);

        Assert.Empty((await store.LoadAsync()).Jobs);
        await using var connection = await new EngineDatabase(new AppPaths(_root)).OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM JobRuntime;";
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SchedulerPause_PersistsAcrossInstances()
    {
        var paths = new AppPaths(_root);
        Assert.False(await new SchedulerStateStore(new EngineDatabase(paths)).IsPausedAsync());

        await new SchedulerStateStore(new EngineDatabase(paths)).SetPausedAsync(true);

        Assert.True(await new SchedulerStateStore(new EngineDatabase(paths)).IsPausedAsync());
    }
}
