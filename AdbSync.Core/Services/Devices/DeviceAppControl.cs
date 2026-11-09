using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using System.Text;
using System.Text.RegularExpressions;

namespace AdbSync.Core.Services.Devices;

public sealed partial class DeviceAppControl(IAdbClient adbClient) : IDeviceAppControl
{
    public async Task ForceStopAsync(string appPackage, string serial, CancellationToken ct = default) =>
        await ShellAsync($"am force-stop {Validate(appPackage)}", serial, ct);

    public async Task<bool> LaunchAsync(string appPackage, string serial, CancellationToken ct = default)
    {
        var output = await ShellAsync($"monkey -p {Validate(appPackage)} -c android.intent.category.LAUNCHER 1", serial, ct);
        return !output.Contains("No activities found", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ShellAsync(string command, string serial, CancellationToken ct)
    {
        var receiver = new ConsoleOutputReceiver();
        var device = new DeviceData { Serial = serial, State = DeviceState.Online };
        await adbClient.ExecuteRemoteCommandAsync(command, device, receiver, Encoding.UTF8, ct);
        return receiver.ToString();
    }

    private static string Validate(string appPackage) =>
        PackagePattern().IsMatch(appPackage) ? appPackage : throw new ArgumentException($"'{appPackage}' isn't a valid Android package name.", nameof(appPackage));

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")]
    private static partial Regex PackagePattern();
}
