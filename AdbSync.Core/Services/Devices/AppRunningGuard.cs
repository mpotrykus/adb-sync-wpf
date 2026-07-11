using AdbSync.Core.Models.Devices;
using System.Text;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;

namespace AdbSync.Core.Services.Devices;

public sealed class AppRunningGuard(IAdbClient adbClient) : IAppRunningGuard
{
    public async Task<bool> IsRunningAnywhereAsync(string appPackage, IEnumerable<string> deviceSerials, CancellationToken ct = default)
    {
        foreach (var serial in deviceSerials)
        {
            var receiver = new ConsoleOutputReceiver();
            var device = new DeviceData { Serial = serial, State = DeviceState.Online };
            await adbClient.ExecuteRemoteCommandAsync($"pidof {appPackage}", device, receiver, Encoding.UTF8, ct);

            if (!string.IsNullOrWhiteSpace(receiver.ToString()))
                return true;
        }

        return false;
    }
}
