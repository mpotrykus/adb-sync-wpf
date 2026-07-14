using AdbSync.Core.Models.Devices;
using AdbSync.Core.Services.Transfer;

namespace AdbSync.Core.Services.Devices;

public interface IDeviceChangeWatcher
{
    /// <summary>Checks whether "adb shell" can both run inotifyd and read the given path on this device - the two things a live watch needs.</summary>
    Task<WatchAvailability> CheckAvailabilityAsync(string serial, string remotePath, IExcludeMatcher exclude, CancellationToken ct = default);

    /// <summary>
    /// Streams one <see cref="ChangeSignal"/> per filesystem event inotifyd reports under any of <paramref name="paths"/>.
    /// Runs until the connection drops or <paramref name="ct"/> is cancelled - callers own reconnect/retry.
    /// </summary>
    IAsyncEnumerable<ChangeSignal> WatchAsync(string serial, IReadOnlyList<string> paths, CancellationToken ct = default);

    /// <summary>Lists <paramref name="remotePath"/> and every directory beneath it, minus anything <paramref name="exclude"/> matches - inotifyd itself isn't recursive, so callers re-seed the watch list with this periodically to pick up newly created folders.</summary>
    Task<IReadOnlyList<string>> EnumerateSubdirectoriesAsync(string serial, string remotePath, IExcludeMatcher exclude, CancellationToken ct = default);

    /// <summary>Cheap point-in-time fingerprint of everything under <paramref name="remotePath"/>, for the polling fallback when live watching isn't available - callers just compare successive snapshots for equality.</summary>
    Task<string> SnapshotAsync(string serial, string remotePath, CancellationToken ct = default);
}
