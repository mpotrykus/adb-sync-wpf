using System.Runtime.CompilerServices;
using System.Text;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;

namespace AdbSync.Core.Devices;

/// <summary>
/// Watches a device folder for changes via toybox's "inotifyd", invoked over the same adb shell connection the
/// rest of the app already uses - no companion app, no root. Only works for paths adb shell can still read post
/// scoped-storage (shared/public storage; an app-private "Android/data/&lt;pkg&gt;" dir is unreliable from Android
/// 11 on and largely inaccessible by 13/14), which is exactly what <see cref="CheckAvailabilityAsync"/> probes for.
/// </summary>
public sealed class DeviceChangeWatcher(IAdbClient adbClient) : IDeviceChangeWatcher
{
    public async Task<WatchAvailability> CheckAvailabilityAsync(string serial, string remotePath, CancellationToken ct = default)
    {
        var device = new DeviceData { Serial = serial, State = DeviceState.Online };

        var inotifyd = await RunAsync(device, "command -v inotifyd", ct);
        if (string.IsNullOrWhiteSpace(inotifyd))
            return new WatchAvailability(false, "inotifyd not present on this device's shell");

        var readable = await RunAsync(device, $"test -d {Quote(remotePath)} && test -r {Quote(remotePath)} && echo OK", ct);
        if (!readable.Contains("OK", StringComparison.Ordinal))
            return new WatchAvailability(false, "path not readable by adb shell (scoped storage - likely an app-private folder)");

        return new WatchAvailability(true, "Live watch supported");
    }

    public async IAsyncEnumerable<ChangeSignal> WatchAsync(
        string serial, IReadOnlyList<string> paths, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (paths.Count == 0)
            yield break;

        var device = new DeviceData { Serial = serial, State = DeviceState.Online };

        // "-" as PROG means "no program to run, just print each event to stdout" (toybox mirrors busybox here).
        var command = "inotifyd - " + string.Join(' ', paths.Select(Quote));

        await foreach (var _ in adbClient.ExecuteRemoteEnumerableAsync(command, device, Encoding.UTF8, ct))
            yield return new ChangeSignal();
    }

    public async Task<IReadOnlyList<string>> EnumerateSubdirectoriesAsync(string serial, string remotePath, CancellationToken ct = default)
    {
        var device = new DeviceData { Serial = serial, State = DeviceState.Online };
        var output = await RunAsync(device, $"find {Quote(remotePath)} -type d", ct);
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .ToList();
    }

    public async Task<string> SnapshotAsync(string serial, string remotePath, CancellationToken ct = default)
    {
        var device = new DeviceData { Serial = serial, State = DeviceState.Online };

        // "name:size:mtime" per file is enough to notice additions, deletions, and content/metadata changes; the
        // exact find/stat flags may need adjusting for a given OEM's toybox build - isolated here for a one-line fix.
        var output = await RunAsync(device, $"find {Quote(remotePath)} -exec stat -c '%n:%s:%Y' {{}} +", ct);

        // Android's shared storage is FUSE-emulated, so the directory-entry order "find" walks in isn't guaranteed
        // stable between successive calls even when nothing changed. Sorting makes the snapshot comparison in
        // RunPollLoopAsync order-independent, so that reordering alone doesn't look like a change and re-trigger
        // the job on every poll.
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Array.Sort(lines, StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    private async Task<string> RunAsync(DeviceData device, string command, CancellationToken ct)
    {
        var receiver = new ConsoleOutputReceiver();
        await adbClient.ExecuteRemoteCommandAsync(command, device, receiver, Encoding.UTF8, ct);
        return receiver.ToString() ?? "";
    }

    private static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";
}
