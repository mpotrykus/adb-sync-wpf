using MudBlazor;

namespace AdbSync.Web.Services;

public static class Format
{
    public static Color RunColor(string? status) => status switch
    {
        "completed" or "completedNoChanges" => Color.Success,
        "dryRunCompleted" => Color.Info,
        "failed" => Color.Error,
        "cancelled" or "skipped" or "skippedAppRunning" => Color.Warning,
        "queued" or "running" => Color.Primary,
        _ => Color.Default,
    };

    public static string RunLabel(string? status) => status switch
    {
        "completed" => "Completed",
        "completedNoChanges" => "No changes",
        "dryRunCompleted" => "Dry run done",
        "failed" => "Failed",
        "cancelled" => "Cancelled",
        "skipped" => "Skipped",
        "skippedAppRunning" => "Skipped (app open)",
        "queued" => "Queued",
        "running" => "Running",
        null => "Never run",
        _ => Words(status),
    };

    public static string Words(string? camel)
    {
        if (string.IsNullOrEmpty(camel))
            return "";
        var chars = new System.Text.StringBuilder();
        for (var i = 0; i < camel.Length; i++)
        {
            var c = camel[i];
            if (i == 0)
                chars.Append(char.ToUpperInvariant(c));
            else if (char.IsUpper(c))
                chars.Append(' ').Append(char.ToLowerInvariant(c));
            else
                chars.Append(c);
        }
        return chars.ToString();
    }

    public static string Ago(DateTimeOffset? at)
    {
        if (at is null)
            return "never";
        var span = DateTimeOffset.Now - at.Value;
        if (span < TimeSpan.Zero)
            return "in " + Span(-span);
        return span.TotalSeconds < 60 ? "just now" : Span(span) + " ago";
    }

    private static string Span(TimeSpan s) =>
        s.TotalMinutes < 60 ? $"{(int)Math.Max(1, s.TotalMinutes)} min"
        : s.TotalHours < 24 ? $"{(int)s.TotalHours} h"
        : $"{(int)s.TotalDays} d";

    public static string Local(DateTimeOffset? at) => at?.ToLocalTime().ToString("ddd d MMM, HH:mm:ss") ?? "-";

    public static string Bytes(long? bytes) => bytes switch
    {
        null => "-",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public static string Duration(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is null)
            return "-";
        var span = (end ?? DateTimeOffset.Now) - start.Value;
        return span.TotalMinutes < 1 ? $"{span.TotalSeconds:0} s" : $"{(int)span.TotalMinutes} min {span.Seconds} s";
    }
}
