using System.Globalization;

namespace AdbSync.Core.Services.Logging;

/// <summary>Narrows a captured run log (see <c>RunLogScope</c>) to the lines about one device, optionally within a time window.</summary>
public static class RunLogFilter
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";
    private const int TimestampLength = 23;
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(50);

    public static string Filter(string logText, string deviceName, DateTimeOffset? from, DateTimeOffset? to)
    {
        var needle = $"'{deviceName}'";
        var kept = new List<string>();
        var include = false;

        foreach (var line in logText.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (TryParseTimestamp(line, out var timestamp))
            {
                include = line.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    && (from is null || timestamp >= from.Value.LocalDateTime - Tolerance)
                    && (to is null || timestamp <= to.Value.LocalDateTime + Tolerance);
            }

            if (include)
                kept.Add(line);
        }

        return string.Join(Environment.NewLine, kept);
    }

    private static bool TryParseTimestamp(string line, out DateTime timestamp)
    {
        timestamp = default;
        return line.Length > TimestampLength + 1
            && line[0] == '['
            && line[TimestampLength + 1] == ']'
            && DateTime.TryParseExact(line.AsSpan(1, TimestampLength), TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    }
}
