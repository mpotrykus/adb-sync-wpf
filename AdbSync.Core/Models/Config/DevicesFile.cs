namespace AdbSync.Core.Models.Config;

/// <summary>On-disk shape of config\devices.json.</summary>
public sealed class DevicesFile
{
    public List<DeviceConfig> Devices { get; set; } = [];
}
