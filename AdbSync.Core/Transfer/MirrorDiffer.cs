namespace AdbSync.Core.Transfer;

public sealed class MirrorDiffer : IMirrorDiffer
{
    /// <summary>Filesystem mtime precision varies (NTFS/ext4/FAT, adb pull -a rounding) - treat close timestamps as unchanged.</summary>
    private static readonly TimeSpan ModifiedTolerance = TimeSpan.FromSeconds(2);

    public MirrorPlan Diff(IReadOnlyList<FileEntry> source, IReadOnlyList<FileEntry> destination)
    {
        var destByPath = destination.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var sourcePaths = new HashSet<string>(source.Select(e => e.RelativePath), StringComparer.Ordinal);

        var toCopy = source
            .Where(s => !destByPath.TryGetValue(s.RelativePath, out var d) || !IsSame(s, d))
            .ToList();

        var extraDestPaths = destination
            .Where(d => !sourcePaths.Contains(d.RelativePath))
            .Select(d => d.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        var topMostExtras = extraDestPaths
            .Where(p => !RelativePathUtil.HasAncestorIn(p, extraDestPaths))
            .ToHashSet(StringComparer.Ordinal);
        var toDelete = destination.Where(d => topMostExtras.Contains(d.RelativePath)).ToList();

        return new MirrorPlan(toCopy, toDelete);
    }

    private static bool IsSame(FileEntry source, FileEntry destination)
    {
        if (source.IsDirectory != destination.IsDirectory)
            return false;
        if (source.IsDirectory)
            return true;

        return source.Size == destination.Size
            && (source.ModifiedUtc - destination.ModifiedUtc).Duration() <= ModifiedTolerance;
    }
}
