using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using System.Text;

namespace AdbSync.Core.Services.Devices;

public sealed class AppRunningGuard(IAdbClient adbClient, TimeSpan? pollInterval = null) : IAppRunningGuard
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _pollInterval = pollInterval ?? DefaultPollInterval;

    public async Task<string?> FindRunningSerialAsync(string appPackage, IEnumerable<string> deviceSerials, CancellationToken ct = default)
    {
        foreach (var serial in deviceSerials)
        {
            if (await IsRunningAsync(appPackage, serial, ct))
                return serial;
        }

        return null;
    }

    public async Task WaitUntilStoppedAsync(string appPackage, string serial, CancellationToken ct = default)
    {
        while (await IsRunningAsync(appPackage, serial, ct))
            await Task.Delay(_pollInterval, ct);
    }

    // pidof alone reports frozen cached processes that have no activities left, which are not "open".
    private async Task<bool> IsRunningAsync(string appPackage, string serial, CancellationToken ct)
    {
        var receiver = new ConsoleOutputReceiver();
        var device = new DeviceData { Serial = serial, State = DeviceState.Online };
        var command = $"pidof {appPackage} >/dev/null && dumpsys activity activities 2>/dev/null | grep -q 'ActivityRecord{{[^}}]* u[0-9]* {appPackage}/' && echo open";
        await adbClient.ExecuteRemoteCommandAsync(command, device, receiver, Encoding.UTF8, ct);
        return !string.IsNullOrWhiteSpace(receiver.ToString());
    }
}
