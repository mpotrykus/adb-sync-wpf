namespace AdbSync.Core.Config.Legacy;

/// <summary>Matches the old PowerShell tool's devices.json shape exactly, for import purposes only.</summary>
public sealed class LegacyDevicesFile
{
    public List<LegacyDevice> Devices { get; set; } = [];
}

public sealed class LegacyDevice
{
    public string Name { get; set; } = string.Empty;
    public string? Ip { get; set; }
    public string? Serial { get; set; }
}
