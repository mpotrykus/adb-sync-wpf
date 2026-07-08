using AdbSync.Core.Config;
using AdbSync.Core.Devices;

namespace AdbSync.Core.Tests.Orchestration.Fakes;

public sealed class FakeDeviceResolver : IAdbDeviceResolver
{
    public Task<string> EnsureConnectedAsync(DeviceConfig device, CancellationToken ct = default) =>
        Task.FromResult(device.Serial ?? device.Ip ?? throw new InvalidOperationException($"Device '{device.Name}' has neither Serial nor Ip."));

    public Task<string> PairAsync(DeviceConfig device, string pairingCode, CancellationToken ct = default) =>
        throw new NotImplementedException();
}
