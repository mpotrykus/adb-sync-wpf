namespace AdbSync.Core.Devices;

public sealed class DeviceConnectException(string deviceName, string ip)
    : Exception($"Could not connect to device '{deviceName}' at {ip} via WiFi ADB.")
{
    public string DeviceName { get; } = deviceName;
    public string Ip { get; } = ip;
}
