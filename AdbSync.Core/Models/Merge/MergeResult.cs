namespace AdbSync.Core.Models.Merge;

public sealed record MergeResult(int Created, int Updated, int Deleted, IReadOnlyList<MergeConflict> Conflicts, SyncManifest UpdatedManifest)
{
    public bool AnyChange => Created + Updated + Deleted + Conflicts.Count > 0;
}
