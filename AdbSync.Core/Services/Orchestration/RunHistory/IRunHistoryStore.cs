using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;

namespace AdbSync.Core.Services.Orchestration.RunHistory;

public interface IRunHistoryStore
{
    /// <summary>Saves the run and trims the job's history down to <paramref name="maxRuns"/> most-recent rows.</summary>
    Task SaveRunAsync(JobRunRecord record, string logText, int maxRuns, CancellationToken ct = default);

    Task<IReadOnlyList<JobRunRecord>> ListRunsAsync(string jobName, CancellationToken ct = default);

    Task<string?> GetRunLogAsync(string jobName, Guid runId, CancellationToken ct = default);
}
