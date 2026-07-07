namespace AdbSync.Core.Transfer;

/// <summary>ToCopy entries are relative to the source tree; ToDelete entries are relative to (and already exist in) the destination tree.</summary>
public sealed record MirrorPlan(IReadOnlyList<FileEntry> ToCopy, IReadOnlyList<FileEntry> ToDelete);
