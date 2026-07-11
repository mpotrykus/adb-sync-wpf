namespace AdbSync.Core.Models.Orchestration;

public enum JobRunOutcome { Completed, CompletedNoChanges, Skipped, SkippedAppRunning, Failed }

public sealed record JobRunResult(JobRunOutcome Outcome, string? ErrorMessage = null);
