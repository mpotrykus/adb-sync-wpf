using Microsoft.Extensions.Logging;

namespace AdbSync.Core.Services.Logging;

/// <summary>Forwards every log call to the real logger, and also mirrors it into the ambient <see cref="RunLogCapture"/> scope, if any.</summary>
internal sealed class RunCapturingLogger<T>(ILogger<T> inner) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        inner.Log(logLevel, eventId, state, exception, formatter);
        RunLogCapture.Record(logLevel, formatter(state, exception), exception);
    }
}
