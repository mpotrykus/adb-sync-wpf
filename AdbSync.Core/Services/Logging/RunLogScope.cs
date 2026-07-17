using Microsoft.Extensions.Logging;

namespace AdbSync.Core.Services.Logging;

/// <summary>Accumulates the log lines recorded for one run; disposing ends capture for the ambient scope.</summary>
internal sealed class RunLogScope(string? jobName = null, ILiveRunLogSink? liveSink = null) : IDisposable
{
    private readonly List<string> _lines = [];

    public void Append(LogLevel level, string message, Exception? exception)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [{LevelTag(level)}] {message}";
        var fullLine = exception is null ? line : $"{line}{Environment.NewLine}{exception}";
        lock (_lines)
            _lines.Add(fullLine);
        if (jobName is not null)
            liveSink?.Append(jobName, fullLine);
    }

    public string BuildText()
    {
        lock (_lines)
            return string.Join(Environment.NewLine, _lines);
    }

    public void Dispose()
    {
        RunLogCapture.End();
        if (jobName is not null)
            liveSink?.End(jobName);
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };
}
