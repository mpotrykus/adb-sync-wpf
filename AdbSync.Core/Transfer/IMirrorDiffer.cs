namespace AdbSync.Core.Transfer;

public interface IMirrorDiffer
{
    /// <summary>Diffs two file trees by relative path; direction-agnostic - "source" mirrors onto "destination".</summary>
    MirrorPlan Diff(IReadOnlyList<FileEntry> source, IReadOnlyList<FileEntry> destination);
}
