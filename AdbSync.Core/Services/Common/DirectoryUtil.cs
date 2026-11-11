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
            ClearReadOnly(new FileInfo(target));
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
            ClearReadOnly(entry);
        ClearReadOnly(new DirectoryInfo(path));

        Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// Best effort off Windows: there ReadOnly maps to a chmod, which some mounts (CIFS, rootless containers) refuse.
    /// If it mattered, the delete or write that follows reports the real error.
    /// </summary>
    private static void ClearReadOnly(FileSystemInfo entry)
    {
        try
        {
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                entry.Attributes &= ~FileAttributes.ReadOnly;
        }
        catch (Exception ex) when (!OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
