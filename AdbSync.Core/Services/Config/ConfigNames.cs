namespace AdbSync.Core.Services.Config;

/// <summary>
/// Rules for job and device names. Both are used as folder and file names on disk, so the same rules apply on
/// every OS: a config written on Windows must stay usable on the Linux server and the other way round.
/// </summary>
public static class ConfigNames
{
    public const int MaxLength = 100;

    private static readonly char[] ForbiddenChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedOnWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>Why <paramref name="name"/> isn't a valid name, or null when it is.</summary>
    public static string? Problem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Name is required.";
        if (name.Length > MaxLength)
            return $"Name can be at most {MaxLength} characters.";
        if (char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]))
            return "Name can't start or end with a space.";
        if (name.IndexOfAny(ForbiddenChars) >= 0 || name.Any(char.IsControl))
            return "Name can't contain < > : \" / \\ | ? * or control characters.";
        if (name is "." or "..")
            return "Name can't be '.' or '..'.";
        if (name.EndsWith('.'))
            return "Name can't end with a dot.";
        var stem = name.Split('.')[0].TrimEnd();
        if (ReservedOnWindows.Contains(stem))
            return $"'{stem}' is a reserved name on Windows.";
        return null;
    }

    /// <summary>True when <paramref name="name"/> differs from every name in <paramref name="existing"/> ignoring case.</summary>
    public static bool IsUnique(string name, IEnumerable<string> existing) =>
        !existing.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns <paramref name="name"/> for use as a path segment, or throws when it isn't a valid name.</summary>
    public static string Segment(string name) =>
        Problem(name) is { } problem
            ? throw new ArgumentException($"'{name}' can't be used as a job or device name. {problem}", nameof(name))
            : name;
}
