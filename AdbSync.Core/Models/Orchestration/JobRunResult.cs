namespace AdbSync.Core.Models.Orchestration;

public enum JobRunOutcome { Completed, CompletedNoChanges, Skipped, SkippedAppRunning, Failed, DryRunCompleted }

public sealed record JobRunResult(JobRunOutcome Outcome, string? ErrorMessage = null);
