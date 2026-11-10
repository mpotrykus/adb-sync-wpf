namespace AdbSync.Core.Models.Merge;

/// <summary>HoldConflicts leaves a conflicting path untouched on both sides (and its manifest baseline as-is) and reports it in <see cref="MergeResult.Held"/> instead of picking a winner.
/// DryRun computes the full result (including what would be created/updated/deleted/conflicted) without touching disk or the manifest.
/// SkipPaths are left untouched on both sides with their manifest baseline kept - used for files whose pull failed, which are
/// missing from staging but must not be read as deletions.</summary>
public sealed record MergeOptions(
    bool BackupConflictLosers = true, string? ConflictBackupDir = null, bool DryRun = false, bool HoldConflicts = false,
    IReadOnlyCollection<string>? SkipPaths = null);
