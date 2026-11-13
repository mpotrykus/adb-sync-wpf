namespace AdbSync.Core.Models.Config;

public sealed class JobDeviceBinding
{
    /// <summary>The bound device's id. Filled in from <see cref="DeviceName"/> when the config is saved.</summary>
    public Guid DeviceId { get; set; }

    /// <summary>Foreign key into <see cref="AppConfig.Devices"/> by <see cref="DeviceConfig.Name"/>.</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>On-device path to sync, normalized with no trailing slash or rsync-style "/." marker.</summary>
    public string RemotePath { get; set; } = string.Empty;
}
