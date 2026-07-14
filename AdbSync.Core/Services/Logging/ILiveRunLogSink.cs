namespace AdbSync.Core.Services.Logging;

/// <summary>Tracks the accumulated log text for whichever run is currently in progress per job, so a UI can tail
/// it live instead of waiting for the run to finish and be persisted to <c>IRunHistoryStore</c>.</summary>
public interface ILiveRunLogSink
{
    void Begin(string jobName);
    void Append(string jobName, string line);
    void End(string jobName);
    bool TryGet(string jobName, out DateTimeOffset startedAt, out string logText);
}
