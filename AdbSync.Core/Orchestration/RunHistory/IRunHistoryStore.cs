namespace AdbSync.Core.Orchestration.RunHistory;

public interface IRunHistoryStore
{
    Task SaveRunAsync(JobRunRecord record, string logText, CancellationToken ct = default);

    Task<IReadOnlyList<JobRunRecord>> ListRunsAsync(string jobName, CancellationToken ct = default);

    Task<string?> GetRunLogAsync(string jobName, Guid runId, CancellationToken ct = default);
}
