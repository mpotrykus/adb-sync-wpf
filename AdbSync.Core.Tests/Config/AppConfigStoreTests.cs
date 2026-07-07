using AdbSync.Core.Config;

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
        Assert.Equal(string.Empty, loaded.Settings.ProjectsDirectory);
    }

    [Fact]
    public async Task SaveAsync_WritesAtomically_NoLeftoverTempFiles()
    {
        var store = new AppConfigStore(new AppPaths(_root));

        await store.SaveAsync(new AppConfig());

        var files = Directory.GetFiles(Path.Combine(_root, "config"));
        Assert.All(files, f => Assert.DoesNotContain(".tmp-", f));
    }
}
