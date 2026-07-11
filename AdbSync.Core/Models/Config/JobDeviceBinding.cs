namespace AdbSync.Core.Models.Config;

public sealed class JobDeviceBinding
{
    /// <summary>Foreign key into <see cref="AppConfig.Devices"/> by <see cref="DeviceConfig.Name"/>.</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>On-device path to sync, normalized with no trailing slash or rsync-style "/." marker.</summary>
    public string RemotePath { get; set; } = string.Empty;
}
