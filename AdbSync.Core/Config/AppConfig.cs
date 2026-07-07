namespace AdbSync.Core.Config;

public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;
    public GlobalSettings Settings { get; set; } = new();
    public List<DeviceConfig> Devices { get; set; } = [];
    public List<SyncJobConfig> Jobs { get; set; } = [];
}
