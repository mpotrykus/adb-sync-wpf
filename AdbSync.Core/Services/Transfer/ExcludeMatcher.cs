using System.Text;
using System.Text.RegularExpressions;
using AdbSync.Core.Models.Transfer;

namespace AdbSync.Core.Services.Transfer;

/// <summary>
/// Preserves the old tool's two exclude pattern shapes: a bare name (e.g. "Cache") matches that name at any
/// depth; a path-shaped pattern (e.g. "Painter/Cache") matches that relative sub-path anchored from the sync root.
/// Either shape may contain glob wildcards (<c>*</c> matches zero or more characters within a path segment,
/// <c>?</c> matches exactly one), e.g. "*.tmp" or "Painter/*.pntr_archive.zip". Literal patterns are matched
/// via a plain set/prefix check; only patterns that actually contain a wildcard pay for regex matching.
/// </summary>
public sealed class ExcludeMatcher : IExcludeMatcher
{
    private readonly HashSet<string> _segmentPatterns;
    private readonly Regex[] _segmentGlobPatterns;
    private readonly string[] _pathPatterns;
    private readonly Regex[] _pathGlobPatterns;

    public ExcludeMatcher(IEnumerable<string> patterns)
    {
        var normalized = patterns
            .Select(p => p.Replace('\\', '/').Trim('/'))
            .Where(p => p.Length > 0)
            .ToArray();

        var segments = normalized.Where(p => !p.Contains('/')).ToArray();
        var paths = normalized.Where(p => p.Contains('/')).ToArray();

        _segmentPatterns = new HashSet<string>(segments.Where(p => !HasWildcard(p)), StringComparer.Ordinal);
        _segmentGlobPatterns = segments.Where(HasWildcard).Select(ToSegmentRegex).ToArray();

        _pathPatterns = paths.Where(p => !HasWildcard(p)).ToArray();
        _pathGlobPatterns = paths.Where(HasWildcard).Select(ToPathRegex).ToArray();
    }

    public bool IsExcluded(string relativePath, bool isDirectory)
    {
        var path = relativePath.Replace('\\', '/').Trim('/');

        if (_segmentPatterns.Count > 0 || _segmentGlobPatterns.Length > 0)
        {
            var pathSegments = path.Split('/');

            if (_segmentPatterns.Count > 0 && pathSegments.Any(_segmentPatterns.Contains))
                return true;

            if (_segmentGlobPatterns.Length > 0 && pathSegments.Any(s => _segmentGlobPatterns.Any(r => r.IsMatch(s))))
                return true;
        }

        foreach (var pattern in _pathPatterns)
        {
            if (path == pattern || path.StartsWith(pattern + "/", StringComparison.Ordinal))
                return true;
        }

        foreach (var regex in _pathGlobPatterns)
        {
            if (regex.IsMatch(path))
                return true;
        }

        return false;
    }

    private static bool HasWildcard(string pattern) => pattern.Contains('*') || pattern.Contains('?');

    private static Regex ToSegmentRegex(string pattern) =>
        new($"^{GlobToRegexBody(pattern)}$", RegexOptions.Compiled);

    private static Regex ToPathRegex(string pattern) =>
        new($"^{GlobToRegexBody(pattern)}(/.*)?$", RegexOptions.Compiled);

    private static string GlobToRegexBody(string pattern)
    {
        var sb = new StringBuilder();

        foreach (var c in pattern)
        {
            switch (c)
            {
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return sb.ToString();
    }
}
