using System.Text;

namespace AdbSync.Core.Services.Companion;

public enum CompanionEventKind
{
    Phase,
    Queued,
    Heartbeat,
    Completed,
    Failed,
    Cancelled,
    Skipped,
}

public sealed record CompanionEvent(
    CompanionEventKind Kind,
    string JobName,
    string? Phase = null,
    string? DeviceName = null,
    string? Message = null,
    bool Pushed = false)
{
    public bool IsTerminal => Kind is CompanionEventKind.Completed or CompanionEventKind.Failed
        or CompanionEventKind.Cancelled or CompanionEventKind.Skipped;
}

/// <summary>Builds the <c>am broadcast</c> shell command that drives the Android companion app's
/// <c>ProgressReceiver</c>. The extra names and event strings must match <c>Protocol.kt</c> in AdbSync.Android.</summary>
public static class CompanionBroadcast
{
    public const string PackageName = "com.adbsync.companion";
    public const string Component = PackageName + "/.ProgressReceiver";
    public const string Action = PackageName + ".EVENT";

    private const int MaxMessageLength = 300;

    public static string BuildCommand(CompanionEvent e)
    {
        var sb = new StringBuilder("am broadcast -n ").Append(Component).Append(" -a ").Append(Action);
        AppendString(sb, "event", e.Kind.ToString().ToLowerInvariant());
        AppendString(sb, "job", e.JobName);
        if (e.Phase is not null)
            AppendString(sb, "phase", e.Phase);
        if (e.DeviceName is not null)
            AppendString(sb, "device", e.DeviceName);
        if (!string.IsNullOrEmpty(e.Message))
            AppendString(sb, "message", Truncate(SingleLine(e.Message)));
        if (e.Pushed)
            sb.Append(" --ez pushed true");
        return sb.ToString();
    }

    /// <summary>Tells the app where to reach the PC's companion server, which bearer token to send, and the
    /// SHA-256 fingerprint of the server certificate to pin.</summary>
    public static string BuildConfigCommand(string url, string token, string certSha256, string pcName)
    {
        var sb = new StringBuilder("am broadcast -n ").Append(Component).Append(" -a ").Append(Action);
        AppendString(sb, "event", "config");
        AppendString(sb, "url", url);
        AppendString(sb, "token", token);
        AppendString(sb, "fp", certSha256);
        AppendString(sb, "pc", pcName);
        return sb.ToString();
    }

    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static void AppendString(StringBuilder sb, string key, string value) =>
        sb.Append(" --es ").Append(key).Append(' ').Append(ShellQuote(value));

    private static string SingleLine(string value) => value.ReplaceLineEndings(" ");

    private static string Truncate(string value) =>
        value.Length <= MaxMessageLength ? value : value[..(MaxMessageLength - 3)] + "...";
}
