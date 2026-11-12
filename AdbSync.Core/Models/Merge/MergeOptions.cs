using AdbSync.Core.Models.Transfer;

namespace AdbSync.Core.Models.Merge;

/// <summary>HoldConflicts leaves a conflicting path untouched on both sides (and its manifest baseline as-is) and reports it in <see cref="MergeResult.Held"/> instead of picking a winner.
/// DryRun computes the full result (including what would be created/updated/deleted/conflicted) without touching disk or the manifest.
/// SkipPaths are left untouched on both sides with their manifest baseline kept - used for files whose pull failed, which are
/// missing from staging but must not be read as deletions.
/// UnchangedOnDevice are device files that an incremental pull did not transfer because they match the manifest baseline: the merge
/// treats them as present on the device (with that size/mtime) but never reads their content from staging.</summary>
public sealed record MergeOptions(
    bool BackupConflictLosers = true, string? ConflictBackupDir = null, bool DryRun = false, bool HoldConflicts = false,
    IReadOnlyCollection<string>? SkipPaths = null, IReadOnlyCollection<FileEntry>? UnchangedOnDevice = null);
