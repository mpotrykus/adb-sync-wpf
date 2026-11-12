using AdbSync.Core.Services.Config;
using AdbSync.Engine.Services;

namespace AdbSync.Server;

public static class DevProjectsDirectory
{
    /// <summary>
    /// A dev server seeded from the Pi inherits the Pi's Linux project paths, which don't exist here. Points the projects
    /// directory (and any per-job override that isn't reachable) at this server's own projects folder so runs have a
    /// master to work with.
    /// </summary>
    public static async Task EnsureLocalAsync(AppConfigService configService, AppPaths paths, ILogger logger)
    {
        var config = await configService.GetAsync();
        var changed = false;

        if (!Path.IsPathFullyQualified(config.Settings.ProjectsDirectory ?? "") || !Directory.Exists(config.Settings.ProjectsDirectory))
        {
            config.Settings.ProjectsDirectory = paths.DefaultProjectsDirectory;
            changed = true;
        }

        foreach (var job in config.Jobs.Where(j => !string.IsNullOrWhiteSpace(j.ProjectDirectory) && !Directory.Exists(j.ProjectDirectory)))
        {
            job.ProjectDirectory = null;
            changed = true;
        }

        ProjectFolders.AdoptRoot(config.Settings.ProjectsDirectory!);
        if (!changed)
            return;

        await configService.SaveAsync();
        logger.LogWarning("Development: projects directory set to {Directory}", config.Settings.ProjectsDirectory);
    }
}
