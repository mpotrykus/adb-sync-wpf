namespace AdbSync.Core.Devices;

public sealed class DeviceConnectException(string deviceName, string ip, string? reason = null)
    : Exception(reason is null
        ? $"Could not connect to device '{deviceName}' at {ip} via WiFi ADB."
        : $"Could not connect to device '{deviceName}' at {ip} via WiFi ADB: {reason}")
{
    public string DeviceName { get; } = deviceName;
    public string Ip { get; } = ip;
}
