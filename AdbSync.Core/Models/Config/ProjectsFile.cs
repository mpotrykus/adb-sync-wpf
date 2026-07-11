namespace AdbSync.Core.Models.Config;

/// <summary>On-disk shape of config\projects.json. "Projects" is kept as the on-disk/UI term for sync jobs.</summary>
public sealed class ProjectsFile
{
    public List<SyncJobConfig> Projects { get; set; } = [];
}
