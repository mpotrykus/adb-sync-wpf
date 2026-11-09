namespace AdbSync.Core.Models.Merge;

public enum MergeChangeKind { Created, Updated, Deleted }

/// <summary><paramref name="ToDevice"/> is true when the change was applied to the device's staging copy, false when applied to master.</summary>
public sealed record MergeFileChange(string RelativePath, MergeChangeKind Kind, long Size, bool ToDevice);
