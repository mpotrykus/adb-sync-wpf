using AdbSync.Core.Models.Config;

namespace AdbSync.Core.Services.Config;

/// <summary>Loads/saves the three on-disk config files (settings/devices/projects) as one composed <see cref="AppConfig"/>.</summary>
public sealed class AppConfigStore(AppPaths paths) : IAppConfigStore
{
    public async Task<AppConfig> LoadAsync(CancellationToken ct = default)
    {
        var settings = await JsonFileIo.ReadAsync(paths.SettingsFile, AppConfigJsonContext.Default.GlobalSettings, ct)
            ?? new GlobalSettings();
        if (string.IsNullOrWhiteSpace(settings.ProjectsDirectory))
            settings.ProjectsDirectory = GlobalSettings.DefaultProjectsDirectory;
        var devicesFile = await JsonFileIo.ReadAsync(paths.DevicesFile, AppConfigJsonContext.Default.DevicesFile, ct)
            ?? new DevicesFile();
        var projectsFile = await JsonFileIo.ReadAsync(paths.ProjectsFile, AppConfigJsonContext.Default.ProjectsFile, ct)
            ?? new ProjectsFile();

        return new AppConfig
        {
            Settings = settings,
            Devices = devicesFile.Devices,
            Jobs = projectsFile.Projects,
        };
    }

    public async Task SaveAsync(AppConfig config, CancellationToken ct = default)
    {
        await JsonFileIo.WriteAtomicAsync(paths.SettingsFile, config.Settings, AppConfigJsonContext.Default.GlobalSettings, ct);
        await JsonFileIo.WriteAtomicAsync(paths.DevicesFile, new DevicesFile { Devices = config.Devices }, AppConfigJsonContext.Default.DevicesFile, ct);
        await JsonFileIo.WriteAtomicAsync(paths.ProjectsFile, new ProjectsFile { Projects = config.Jobs }, AppConfigJsonContext.Default.ProjectsFile, ct);
    }
}
