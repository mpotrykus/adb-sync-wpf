namespace AdbSync.Core.Models.Merge;

/// <summary>A path where both sides had diverged since the manifest baseline; resolved by newer-mtime-wins.</summary>
public sealed record MergeConflict(string RelativePath, string WinningSide, DateTimeOffset? StagingModifiedUtc, DateTimeOffset? MasterModifiedUtc, string? BackupPath);
