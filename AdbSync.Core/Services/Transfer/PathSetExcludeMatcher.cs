namespace AdbSync.Core.Services.Transfer;

/// <summary>Excludes whatever <paramref name="inner"/> does, plus exactly the listed file paths.</summary>
public sealed class PathSetExcludeMatcher(IExcludeMatcher inner, IEnumerable<string> paths) : IExcludeMatcher
{
    private readonly HashSet<string> _paths = new(paths.Select(Normalize), StringComparer.Ordinal);

    public bool IsExcluded(string relativePath, bool isDirectory) =>
        inner.IsExcluded(relativePath, isDirectory) || (!isDirectory && _paths.Contains(Normalize(relativePath)));

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}
