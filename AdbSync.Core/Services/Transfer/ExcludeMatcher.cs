using AdbSync.Core.Models.Transfer;

namespace AdbSync.Core.Services.Transfer;

/// <summary>
/// Preserves the old tool's two exclude pattern shapes: a bare name (e.g. "Cache") matches that name at any
/// depth; a path-shaped pattern (e.g. "Painter/Cache") matches that relative sub-path anchored from the sync root.
/// </summary>
public sealed class ExcludeMatcher : IExcludeMatcher
{
    private readonly HashSet<string> _segmentPatterns;
    private readonly string[] _pathPatterns;

    public ExcludeMatcher(IEnumerable<string> patterns)
    {
        var normalized = patterns
            .Select(p => p.Replace('\\', '/').Trim('/'))
            .Where(p => p.Length > 0)
            .ToArray();

        _segmentPatterns = new HashSet<string>(normalized.Where(p => !p.Contains('/')), StringComparer.Ordinal);
        _pathPatterns = normalized.Where(p => p.Contains('/')).ToArray();
    }

    public bool IsExcluded(string relativePath, bool isDirectory)
    {
        var path = relativePath.Replace('\\', '/').Trim('/');

        if (_segmentPatterns.Count > 0 && path.Split('/').Any(_segmentPatterns.Contains))
            return true;

        foreach (var pattern in _pathPatterns)
        {
            if (path == pattern || path.StartsWith(pattern + "/", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
