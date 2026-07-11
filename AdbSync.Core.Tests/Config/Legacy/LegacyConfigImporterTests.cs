using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Models.Config.Legacy;
using AdbSync.Core.Services.Config.Legacy;

namespace AdbSync.Core.Tests.Config.Legacy;

public class LegacyConfigImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));

    public LegacyConfigImporterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private const string DevicesJson = """
        {
            "devices": [
                { "name": "S23+", "ip": "192.168.0.40" },
                { "name": "BlueStacks", "ip": "127.0.0.1" }
            ]
        }
        """;

    private const string ProjectsJson = """
        {
            "projectsDirectory": "C:\\Users\\mpotr\\Google Drive\\Z - Backups\\adb-sync",
            "projects": [
                {
                    "name": "NomadSculpt",
                    "appPackage": "com.stephaneginier.nomad",
                    "exclude": ["tmp_session", "postprocess", "profiles", "data"],
                    "devices": [
                        { "deviceName": "S23+", "remotePath": "/storage/emulated/0/Android/data/com.stephaneginier.nomad/files/." },
                        { "deviceName": "TabS9", "remotePath": "/storage/emulated/0/Android/data/com.stephaneginier.nomad/files/." }
                    ]
                }
            ]
        }
        """;

    [Fact]
    public async Task ImportAsync_MapsDevicesAndJobs_AndNormalizesRemotePaths()
    {
        var devicesPath = Path.Combine(_root, "devices.json");
        var projectsPath = Path.Combine(_root, "projects.json");
        await File.WriteAllTextAsync(devicesPath, DevicesJson);
        await File.WriteAllTextAsync(projectsPath, ProjectsJson);

        var importer = new LegacyConfigImporter();
        var config = await importer.ImportAsync(devicesPath, projectsPath);

        Assert.Equal(@"C:\Users\mpotr\Google Drive\Z - Backups\adb-sync", config.Settings.ProjectsDirectory);

        Assert.Equal(2, config.Devices.Count);
        Assert.Equal("192.168.0.40", config.Devices.Single(d => d.Name == "S23+").Ip);

        var job = Assert.Single(config.Jobs);
        Assert.Equal("NomadSculpt", job.Name);
        Assert.Equal("com.stephaneginier.nomad", job.AppPackage);
        Assert.Equal(["tmp_session", "postprocess", "profiles", "data"], job.Exclude);
        Assert.Equal(ScheduleKind.Manual, job.Schedule.Kind);
        Assert.True(job.Enabled);

        Assert.All(job.Devices, d => Assert.False(d.RemotePath.EndsWith('.')));
        Assert.Equal(
            "/storage/emulated/0/Android/data/com.stephaneginier.nomad/files",
            job.Devices.Single(d => d.DeviceName == "S23+").RemotePath);
    }

    [Fact]
    public async Task ImportAsync_MissingDevicesFile_Throws()
    {
        var importer = new LegacyConfigImporter();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            importer.ImportAsync(Path.Combine(_root, "missing.json"), Path.Combine(_root, "also-missing.json")));
    }

    [Theory]
    [InlineData("/storage/emulated/0/Android/data/pkg/files/.", "/storage/emulated/0/Android/data/pkg/files")]
    [InlineData("/storage/emulated/0/Android/data/pkg/files/", "/storage/emulated/0/Android/data/pkg/files")]
    [InlineData("/storage/emulated/0/Android/data/pkg/files", "/storage/emulated/0/Android/data/pkg/files")]
    public void NormalizeRemotePath_StripsTrailingSlashDotConvention(string input, string expected)
    {
        Assert.Equal(expected, LegacyConfigImporter.NormalizeRemotePath(input));
    }
}
