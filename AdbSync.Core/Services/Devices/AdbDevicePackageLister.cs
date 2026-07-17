using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using System.Text;

namespace AdbSync.Core.Services.Devices;

public sealed class AdbDevicePackageLister(IAdbClient adbClient) : IDevicePackageLister
{
    public async Task<IReadOnlyList<string>> ListInstalledPackagesAsync(string serial, CancellationToken ct = default)
    {
        var receiver = new ConsoleOutputReceiver();
        var device = new DeviceData { Serial = serial, State = DeviceState.Online };
        await adbClient.ExecuteRemoteCommandAsync("pm list packages -3", device, receiver, Encoding.UTF8, ct);

        return receiver.ToString()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("package:", StringComparison.Ordinal))
            .Select(line => line["package:".Length..].Trim())
            .Where(name => name.Length > 0)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
