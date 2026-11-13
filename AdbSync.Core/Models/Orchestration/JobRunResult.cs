namespace AdbSync.Core.Models.Orchestration;

public enum JobRunOutcome { Completed, CompletedNoChanges, Skipped, SkippedAppRunning, Failed, DryRunCompleted, Cancelled, SkippedProjectFolderUnavailable }

public sealed record JobRunResult(JobRunOutcome Outcome, string? ErrorMessage = null, RunErrorCode? ErrorCode = null);
