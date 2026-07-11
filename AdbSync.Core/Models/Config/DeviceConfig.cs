namespace AdbSync.Core.Models.Config;

public sealed class DeviceConfig
{
    public string Name { get; set; } = string.Empty;

    /// <summary>WiFi/mDNS device. Null when <see cref="Serial"/> is set (legacy USB fallback).</summary>
    public string? Ip { get; set; }

    /// <summary>Static USB serial fallback; bypasses mDNS discovery entirely.</summary>
    public string? Serial { get; set; }

    /// <summary>Last resolved "ip:port" for a WiFi device, persisted so it survives app restarts.</summary>
    public string? CachedHostPort { get; set; }

    public DateTimeOffset? CachedAt { get; set; }
}
