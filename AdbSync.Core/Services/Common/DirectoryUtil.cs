namespace AdbSync.Core.Services.Common;

internal static class DirectoryUtil
{
    public static void CopyRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            File.Copy(file, target, overwrite: true);
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// Cloud-sync folders (Google Drive in particular) stamp directories ReadOnly, which makes a plain recursive
    /// Directory.Delete fail with "access denied". Clear the flag bottom-up before deleting.
    /// </summary>
    public static void DeleteRecursive(string path)
    {
        if (!Directory.Exists(path))
            return;

        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                entry.Attributes &= ~FileAttributes.ReadOnly;
        }

        var root = new DirectoryInfo(path);
        if ((root.Attributes & FileAttributes.ReadOnly) != 0)
            root.Attributes &= ~FileAttributes.ReadOnly;

        Directory.Delete(path, recursive: true);
    }
}
