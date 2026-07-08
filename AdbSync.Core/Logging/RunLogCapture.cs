using Microsoft.Extensions.Logging;

namespace AdbSync.Core.Logging;

/// <summary>
/// Ambient per-run log capture. Safe as an AsyncLocal because jobs run strictly sequentially today
/// (SyncOrchestrator has no concurrent execution) - revisit if concurrent job runs are ever introduced.
/// </summary>
internal static class RunLogCapture
{
    private static readonly AsyncLocal<RunLogScope?> _current = new();

    public static RunLogScope Begin()
    {
        var scope = new RunLogScope();
        _current.Value = scope;
        return scope;
    }

    public static void End() => _current.Value = null;

    public static void Record(LogLevel level, string message, Exception? exception) =>
        _current.Value?.Append(level, message, exception);
}
