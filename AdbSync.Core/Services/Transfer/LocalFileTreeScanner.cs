using AdbSync.Core.Models.Transfer;

namespace AdbSync.Core.Services.Transfer;

public static class LocalFileTreeScanner
{
    public static List<FileEntry> Scan(string rootPath, IExcludeMatcher exclude)
    {
        var entries = new List<FileEntry>();
        if (Directory.Exists(rootPath))
            ScanDirectory(rootPath, rootPath, exclude, entries);
        return entries;
    }

    private static void ScanDirectory(string root, string currentDir, IExcludeMatcher exclude, List<FileEntry> entries)
    {
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(currentDir))
        {
            var relativePath = Path.GetRelativePath(root, entryPath).Replace('\\', '/');
            var isDirectory = Directory.Exists(entryPath);
            if (exclude.IsExcluded(relativePath, isDirectory))
                continue;

            if (isDirectory)
            {
                entries.Add(new FileEntry(relativePath, true, 0, new DateTimeOffset(Directory.GetLastWriteTimeUtc(entryPath))));
                ScanDirectory(root, entryPath, exclude, entries);
            }
            else
            {
                var info = new FileInfo(entryPath);
                entries.Add(new FileEntry(relativePath, false, info.Length, new DateTimeOffset(info.LastWriteTimeUtc)));
            }
        }
    }
}
