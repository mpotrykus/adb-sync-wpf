namespace AdbSync.Core.Transfer;

/// <summary>
/// Hand-rolled recursive walk over repeated single-directory LIST calls, since the ADB sync protocol has no
/// recursive listing verb. Excluded subtrees are never even listed, matching the old tool's behavior of skipping
/// excluded directories entirely rather than listing-then-filtering.
/// </summary>
public static class RemoteFileTreeScanner
{
    public static async Task<List<FileEntry>> ScanAsync(
        IRemoteFileSystem remote, string rootPath, IExcludeMatcher exclude, CancellationToken ct = default)
    {
        var entries = new List<FileEntry>();
        await ScanDirectoryAsync(remote, rootPath, "", exclude, entries, ct);
        return entries;
    }

    private static async Task ScanDirectoryAsync(
        IRemoteFileSystem remote, string rootPath, string relativeDir, IExcludeMatcher exclude, List<FileEntry> entries, CancellationToken ct)
    {
        var currentPath = relativeDir.Length == 0 ? rootPath : $"{rootPath}/{relativeDir}";
        var children = await remote.ListDirectoryAsync(currentPath, ct);

        foreach (var child in children)
        {
            var relativePath = relativeDir.Length == 0 ? child.Name : $"{relativeDir}/{child.Name}";
            if (exclude.IsExcluded(relativePath, child.IsDirectory))
                continue;

            if (child.IsDirectory)
            {
                entries.Add(new FileEntry(relativePath, true, 0, child.ModifiedUtc));
                await ScanDirectoryAsync(remote, rootPath, relativePath, exclude, entries, ct);
            }
            else
            {
                entries.Add(new FileEntry(relativePath, false, child.Size, child.ModifiedUtc));
            }
        }
    }
}
