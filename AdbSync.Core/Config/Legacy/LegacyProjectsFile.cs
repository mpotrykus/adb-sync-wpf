namespace AdbSync.Core.Config.Legacy;

/// <summary>Matches the old PowerShell tool's projects.json shape exactly, for import purposes only.</summary>
public sealed class LegacyProjectsFile
{
    public string ProjectsDirectory { get; set; } = string.Empty;
    public List<LegacyProject> Projects { get; set; } = [];
}

public sealed class LegacyProject
{
    public string Name { get; set; } = string.Empty;
    public string? AppPackage { get; set; }
    public List<string> Exclude { get; set; } = [];
    public List<LegacyProjectDevice> Devices { get; set; } = [];
}

public sealed class LegacyProjectDevice
{
    public string DeviceName { get; set; } = string.Empty;
    public string RemotePath { get; set; } = string.Empty;
}
