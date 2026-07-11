using AdbSync.Core.Models.Transfer;

namespace AdbSync.Core.Services.Transfer;

internal static class RelativePathUtil
{
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
