using System.Collections.Concurrent;
using System.Text;

namespace AdbSync.Core.Services.Logging;

public sealed class LiveRunLogSink : ILiveRunLogSink
{
    private sealed class Entry
    {
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public StringBuilder Text { get; } = new();
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public void Begin(string jobName) => _entries[jobName] = new Entry();

    public void Append(string jobName, string line)
    {
        if (!_entries.TryGetValue(jobName, out var entry))
            return;

        lock (entry.Text)
        {
            if (entry.Text.Length > 0)
                entry.Text.Append(Environment.NewLine);
            entry.Text.Append(line);
        }
    }

    public void End(string jobName) => _entries.TryRemove(jobName, out _);

    public bool TryGet(string jobName, out DateTimeOffset startedAt, out string logText)
    {
        if (!_entries.TryGetValue(jobName, out var entry))
        {
            startedAt = default;
            logText = "";
            return false;
        }

        startedAt = entry.StartedAt;
        lock (entry.Text)
            logText = entry.Text.ToString();
        return true;
    }
}
