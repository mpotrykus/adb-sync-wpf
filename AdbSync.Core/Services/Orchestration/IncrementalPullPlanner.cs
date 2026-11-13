using AdbSync.Core.Models.Merge;
using AdbSync.Core.Models.Transfer;
using AdbSync.Core.Services.Merge;

namespace AdbSync.Core.Services.Orchestration;

/// <summary>Decides which device files an incremental pull may leave on the device. A file is skipped only when the device
/// copy AND the master copy both still match the manifest baseline from the last successful sync - i.e. the merge would
/// classify it as a no-op anyway. Anything unknown or different is pulled.</summary>
public static class IncrementalPullPlanner
{
    /// <summary>True when this device's manifest is trustworthy enough to skip files: it has entries and a full pull was
    /// completed within <paramref name="intervalDays"/>. An interval of 0 or less means always pull everything.</summary>
    public static bool IsEligible(SyncManifest? manifest, int intervalDays, DateTimeOffset now) =>
        manifest is { Entries.Count: > 0, LastFullPullAtUtc: { } lastFull }
        && intervalDays > 0
        && now - lastFull < TimeSpan.FromDays(intervalDays)
        && lastFull <= now;

    public static Func<FileEntry, bool> BuildSkipPredicate(SyncManifest manifest, IReadOnlyDictionary<string, FileEntry> master) =>
        remote =>
            !remote.IsDirectory
            && manifest.Entries.TryGetValue(remote.RelativePath, out var baseline)
            && TwoWayMergeEngine.IsUnchanged(remote, baseline)
            && master.TryGetValue(remote.RelativePath, out var masterEntry)
            && !masterEntry.IsDirectory
            && TwoWayMergeEngine.IsUnchanged(masterEntry, baseline);
}
