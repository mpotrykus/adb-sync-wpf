namespace AdbSync.Core.Merge;

/// <summary>DryRun computes the full result (including what would be created/updated/deleted/conflicted) without touching disk or the manifest.</summary>
public sealed record MergeOptions(bool BackupConflictLosers = true, string? ConflictBackupDir = null, bool DryRun = false);
