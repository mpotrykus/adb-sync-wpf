namespace AdbSync.Core.Config;

public sealed class SyncJobConfig
{
    /// <summary>Folder name under <see cref="GlobalSettings.ProjectsDirectory"/>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Android package id. If set, the whole job is skipped while running on any bound device.</summary>
    public string? AppPackage { get; set; }

    /// <summary>Name/relative-path patterns excluded from both pull and push.</summary>
    public List<string> Exclude { get; set; } = [];

    public List<JobDeviceBinding> Devices { get; set; } = [];
    public JobSchedule Schedule { get; set; } = new();
    public bool Enabled { get; set; } = true;
}
