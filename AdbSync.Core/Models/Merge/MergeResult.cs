namespace AdbSync.Core.Models.Merge;

public sealed record MergeResult(int Created, int Updated, int Deleted, IReadOnlyList<MergeConflict> Conflicts, SyncManifest UpdatedManifest, IReadOnlyList<HeldConflict>? HeldConflicts = null, IReadOnlyList<MergeFileChange>? FileChanges = null)
{
    public IReadOnlyList<HeldConflict> Held { get; } = HeldConflicts ?? [];

    /// <summary>Every non-conflicting file the merge created, updated or deleted; conflicts are in <see cref="Conflicts"/> and <see cref="Held"/>.</summary>
    public IReadOnlyList<MergeFileChange> Changes { get; } = FileChanges ?? [];

    public bool AnyChange => Created + Updated + Deleted + Conflicts.Count > 0;
}
