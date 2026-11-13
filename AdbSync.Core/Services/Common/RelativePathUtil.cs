namespace AdbSync.Core.Services.Common;

internal static class RelativePathUtil
{
    /// <summary>
    /// Converts OS separators to '/'. Only Windows uses a backslash as a separator; on Linux it's a legal filename
    /// character, so replacing it there would corrupt names.
    /// </summary>
    public static string ToForwardSlashes(string path) =>
        Path.DirectorySeparatorChar == '\\' ? path.Replace('\\', '/') : path;

    /// <summary>True if any ancestor directory of <paramref name="relativePath"/> (using '/' separators) is itself in <paramref name="set"/>.</summary>
    public static bool HasAncestorIn(string relativePath, IReadOnlySet<string> set)
    {
        var idx = relativePath.LastIndexOf('/');
        while (idx > 0)
        {
            var parent = relativePath[..idx];
            if (set.Contains(parent))
                return true;
            idx = relativePath.LastIndexOf('/', idx - 1);
        }
        return false;
    }
}
