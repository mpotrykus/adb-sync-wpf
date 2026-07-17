using AdbSync.Core.Models.Devices;
using AdbSync.Core.Services.Transfer;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using System.Runtime.CompilerServices;
using System.Text;

namespace AdbSync.Core.Services.Devices;

/// <summary>
/// Watches a device folder for changes via toybox's "inotifyd", invoked over the same adb shell connection the
/// rest of the app already uses - no companion app, no root. Only works for paths adb shell can still read post
/// scoped-storage (shared/public storage; an app-private "Android/data/&lt;pkg&gt;" dir is unreliable from Android
/// 11 on and largely inaccessible by 13/14), which is exactly what <see cref="CheckAvailabilityAsync"/> probes for.
/// </summary>
public sealed class DeviceChangeWatcher(IAdbClient adbClient) : IDeviceChangeWatcher
{
    // inotifyd isn't recursive - watching a large tree means registering one inotify watch per directory in a
    // single command. Past a few hundred, registering them all at once floods the adb shell connection with
    // self-triggered events (each new watch looks like an "access" event to its already-registered parent), which
    // *can* break the connection outright under load (e.g. over a slower Wi-Fi hop). This is a recommendation,
    // not a hard requirement - trees past it have been observed to work fine - so it only produces a warning.
    private const int RecommendedMaxWatchedDirectories = 200;

    public async Task<WatchAvailability> CheckAvailabilityAsync(string serial, string remotePath, IExcludeMatcher exclude, CancellationToken ct = default)
    {
        var device = new DeviceData { Serial = serial, State = DeviceState.Online };

        var inotifyd = await RunAsync(device, "command -v inotifyd", ct);
        if (string.IsNullOrWhiteSpace(inotifyd))
            return new WatchAvailability(false, "inotifyd not present on this device's shell");

        var readable = await RunAsync(device, $"test -d {Quote(remotePath)} && test -r {Quote(remotePath)} && echo OK", ct);
        if (!readable.Contains("OK", StringComparison.Ordinal))
            return new WatchAvailability(false, "path not readable by adb shell (scoped storage - likely an app-private folder)");

        var dirs = await EnumerateSubdirectoriesAsync(serial, remotePath, exclude, ct);
        var warning = dirs.Count > RecommendedMaxWatchedDirectories
            ? $"watching {dirs.Count} directories (recommended limit is {RecommendedMaxWatchedDirectories}) - " +
              "inotifyd needs one watch per directory, and a tree this large may flood the connection and drop it under load. " +
              "Consider narrowing the path or excluding more of it."
            : null;

        return new WatchAvailability(true, "Live watch supported", warning);
    }

    public async IAsyncEnumerable<ChangeSignal> WatchAsync(
        string serial, IReadOnlyList<string> paths, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (paths.Count == 0)
            yield break;

        var device = new DeviceData { Serial = serial, State = DeviceState.Online };

        // "-" as PROG means "no program to run, just print each event to stdout" (toybox mirrors busybox here).
        // ":ndmy" restricts each watch to real directory-entry changes (created/deleted/moved-in/moved-out).
        // Without it, toybox's default mask includes "a" (accessed) - a sync run's own pull (which reads every
        // file under the watched tree) triggers a flood of self-generated access events, which without this
        // filter looks indistinguishable from a real change and re-triggers the job the moment it finishes.
        var command = "inotifyd - " + string.Join(' ', paths.Select(p => Quote(p) + ":ndmy"));

        await foreach (var _ in adbClient.ExecuteRemoteEnumerableAsync(command, device, Encoding.UTF8, ct))
            yield return new ChangeSignal();
    }

    public async Task<IReadOnlyList<string>> EnumerateSubdirectoriesAsync(string serial, string remotePath, IExcludeMatcher exclude, CancellationToken ct = default)
    {
        var device = new DeviceData { Serial = serial, State = DeviceState.Online };
        var output = await RunAsync(device, $"find {Quote(remotePath)} -type d", ct);
        var root = remotePath.TrimEnd('/');
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .Where(path => !exclude.IsExcluded(RelativeToRoot(root, path), isDirectory: true))
            .ToList();
    }

    /// <summary>"/root/sub/dir" relative to root "/root" is "sub/dir"; the root itself is "" - <see cref="IExcludeMatcher"/>
    /// patterns are always expressed relative to the sync root, same as the transfer engine already does.</summary>
    private static string RelativeToRoot(string root, string path) =>
        path == root ? "" : path.StartsWith(root + "/", StringComparison.Ordinal) ? path[(root.Length + 1)..] : path;

    public async Task<string> SnapshotAsync(string serial, string remotePath, CancellationToken ct = default)
    {
        var device = new DeviceData { Serial = serial, State = DeviceState.Online };
        var output = await RunAsync(device, $"find {Quote(remotePath)} -exec stat -c '%n:%s:%Y' {{}} +", ct);
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
