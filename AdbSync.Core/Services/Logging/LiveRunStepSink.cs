using AdbSync.Core.Models.Orchestration.RunHistory;
using System.Collections.Concurrent;

namespace AdbSync.Core.Services.Logging;

public sealed class LiveRunStepSink : ILiveRunStepSink
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<RunStepRecord>> _steps = new();

    public event Action<string>? Changed;

    public void Publish(string jobName, IReadOnlyList<RunStepRecord> steps)
    {
        _steps[jobName] = steps;
        Changed?.Invoke(jobName);
    }

    public void End(string jobName)
    {
        _steps.TryRemove(jobName, out _);
        Changed?.Invoke(jobName);
    }

    public bool TryGet(string jobName, out IReadOnlyList<RunStepRecord> steps)
    {
        if (_steps.TryGetValue(jobName, out var found))
        {
            steps = found;
            return true;
        }
        steps = [];
        return false;
    }
}
