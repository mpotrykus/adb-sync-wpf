namespace AdbSync.Core.Models.Merge;

public sealed record MergeResult(int Created, int Updated, int Deleted, IReadOnlyList<MergeConflict> Conflicts, SyncManifest UpdatedManifest, IReadOnlyList<HeldConflict>? HeldConflicts = null)
{
    public IReadOnlyList<HeldConflict> Held { get; } = HeldConflicts ?? [];

    public bool AnyChange => Created + Updated + Deleted + Conflicts.Count > 0;
}
