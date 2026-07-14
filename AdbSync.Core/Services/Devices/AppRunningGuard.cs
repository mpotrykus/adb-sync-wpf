using AdbSync.Core.Models.Devices;
using System.Text;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;

namespace AdbSync.Core.Services.Devices;

public sealed class AppRunningGuard(IAdbClient adbClient) : IAppRunningGuard
{
    public async Task<string?> FindRunningSerialAsync(string appPackage, IEnumerable<string> deviceSerials, CancellationToken ct = default)
    {
        foreach (var serial in deviceSerials)
        {
            var receiver = new ConsoleOutputReceiver();
            var device = new DeviceData { Serial = serial, State = DeviceState.Online };
            await adbClient.ExecuteRemoteCommandAsync($"pidof {appPackage}", device, receiver, Encoding.UTF8, ct);

            if (!string.IsNullOrWhiteSpace(receiver.ToString()))
                return serial;
        }

        return null;
    }

    public async Task WaitUntilStoppedAsync(string appPackage, string serial, CancellationToken ct = default)
    {
        var receiver = new ConsoleOutputReceiver();
        var device = new DeviceData { Serial = serial, State = DeviceState.Online };
        var command = $"while pidof {appPackage} >/dev/null 2>&1; do sleep 1; done";
        await adbClient.ExecuteRemoteCommandAsync(command, device, receiver, Encoding.UTF8, ct);
    }
}
