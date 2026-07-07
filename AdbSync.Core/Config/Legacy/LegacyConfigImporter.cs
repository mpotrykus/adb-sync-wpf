namespace AdbSync.Core.Config.Legacy;

public sealed class LegacyConfigImporter : ILegacyConfigImporter
{
    public async Task<AppConfig> ImportAsync(string legacyDevicesJsonPath, string legacyProjectsJsonPath, CancellationToken ct = default)
    {
        var legacyDevices = await JsonFileIo.ReadAsync(legacyDevicesJsonPath, AppConfigJsonContext.Default.LegacyDevicesFile, ct)
            ?? throw new FileNotFoundException("Legacy devices.json not found.", legacyDevicesJsonPath);
        var legacyProjects = await JsonFileIo.ReadAsync(legacyProjectsJsonPath, AppConfigJsonContext.Default.LegacyProjectsFile, ct)
            ?? throw new FileNotFoundException("Legacy projects.json not found.", legacyProjectsJsonPath);

        var devices = legacyDevices.Devices
            .Select(d => new DeviceConfig { Name = d.Name, Ip = d.Ip, Serial = d.Serial })
            .ToList();

        var jobs = legacyProjects.Projects
            .Select(p => new SyncJobConfig
            {
                Name = p.Name,
                AppPackage = p.AppPackage,
                Exclude = [.. p.Exclude],
                Devices = p.Devices
                    .Select(pd => new JobDeviceBinding { DeviceName = pd.DeviceName, RemotePath = NormalizeRemotePath(pd.RemotePath) })
                    .ToList(),
                // No scheduling existed in the old tool - the user opts each job into the new scheduler explicitly.
                Schedule = new JobSchedule { Kind = ScheduleKind.Manual },
                Enabled = true,
            })
            .ToList();

        return new AppConfig
        {
            Settings = new GlobalSettings { ProjectsDirectory = legacyProjects.ProjectsDirectory },
            Devices = devices,
            Jobs = jobs,
        };
    }

    /// <summary>Strips the rsync-style trailing "/." convention the old tool used; the new engine always syncs directory contents.</summary>
    public static string NormalizeRemotePath(string remotePath)
    {
        var path = remotePath.Trim();
        if (path.EndsWith("/.", StringComparison.Ordinal))
            path = path[..^2];
        return path.TrimEnd('/');
    }
}
