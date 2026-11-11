using AdbSync.Core.Services.Logging;
using AdbSync.Server.Api;
using System.Collections.Concurrent;

namespace AdbSync.Server.Events;

/// <summary>
/// The engine's live log, plus a <c>run.log</c> event per line on the opt-in topic <c>run.log:{runId}</c>, so a client
/// tails a run without polling. Lines are numbered from 0 per run (<c>fromLine</c>), so a client can detect a gap.
/// </summary>
public sealed class PublishingRunLogSink(LiveRunLogSink inner, EventBus bus, RunRegistry runs) : ILiveRunLogSink
{
    private readonly ConcurrentDictionary<string, int> _lineCounts = new(StringComparer.Ordinal);

    public void Begin(string jobName)
    {
        _lineCounts[jobName] = 0;
        inner.Begin(jobName);
    }

    public void Append(string jobName, string line)
    {
        inner.Append(jobName, line);
        var number = _lineCounts.AddOrUpdate(jobName, 1, (_, n) => n + 1) - 1;
        if (runs.Active.FirstOrDefault(r => r.JobName == jobName) is { } run)
            bus.Publish("run.log", new { runId = run.Id, fromLine = number, lines = new[] { line } }, topic: $"run.log:{run.Id}");
    }

    public void End(string jobName)
    {
        _lineCounts.TryRemove(jobName, out _);
        inner.End(jobName);
    }

    public bool TryGet(string jobName, out DateTimeOffset startedAt, out string logText) => inner.TryGet(jobName, out startedAt, out logText);
}
