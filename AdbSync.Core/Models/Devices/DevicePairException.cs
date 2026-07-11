namespace AdbSync.Core.Models.Devices;

public sealed class DevicePairException(string deviceName, string ip, string reason)
    : Exception($"Could not pair with device '{deviceName}' at {ip}: {reason}")
{
    public string DeviceName { get; } = deviceName;
    public string Ip { get; } = ip;
}
