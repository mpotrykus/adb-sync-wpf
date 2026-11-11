using AdbSync.Core.Models.Orchestration.RunHistory;

namespace AdbSync.Core.Services.Orchestration.RunHistory;

public interface IRunHistoryStore
{
    /// <summary>Saves the run and trims the job's history down to <paramref name="maxRuns"/> most-recent rows.</summary>
    Task SaveRunAsync(JobRunRecord record, string logText, int maxRuns, CancellationToken ct = default) =>
        SaveRunAsync(record, logText, maxRuns, null, ct);

    Task SaveRunAsync(JobRunRecord record, string logText, int maxRuns, IReadOnlyList<RunStepRecord>? steps, CancellationToken ct = default) =>
        SaveRunAsync(record, logText, maxRuns, steps, null, 0, ct);

    /// <summary>Also stores the run's per-file changes. Only the newest <paramref name="keepFileDetailRuns"/> runs of the job keep their file rows (0 keeps every run's).</summary>
    Task SaveRunAsync(
        JobRunRecord record, string logText, int maxRuns, IReadOnlyList<RunStepRecord>? steps,
        IReadOnlyList<RunFileChange>? files, int keepFileDetailRuns, CancellationToken ct = default);

    Task<IReadOnlyList<JobRunRecord>> ListRunsAsync(string jobName, CancellationToken ct = default);

    Task<IReadOnlyList<RunFileChange>> GetRunFilesAsync(Guid runId, CancellationToken ct = default);

    Task<IReadOnlyList<RunStepRecord>> GetRunStepsAsync(Guid runId, CancellationToken ct = default);

    Task<string?> GetRunLogAsync(string jobName, Guid runId, CancellationToken ct = default);

    /// <summary>The run with this id, from any job, or null.</summary>
    Task<JobRunRecord?> GetRunAsync(Guid runId, CancellationToken ct = default);

    /// <summary>Records how a run was started (API values such as dryRun / schedule), which the runner itself doesn't know.</summary>
    Task SetRunContextAsync(Guid runId, string mode, string trigger, Guid? requestedBy, CancellationToken ct = default);

    /// <summary>Moves a renamed job's history to its new name, so it stays attached to the job.</summary>
    Task RenameJobAsync(string oldName, string newName, CancellationToken ct = default);
}
