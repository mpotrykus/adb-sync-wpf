using AdbSync.Core.Models.Orchestration;

namespace AdbSync.Server.Api;

/// <summary>Engine values in the contract's wire form (lowerCamelCase enums, snake_case error codes).</summary>
public static class Contract
{
    public static string SyncPhase(SyncPhase phase) => phase switch
    {
        Core.Models.Orchestration.SyncPhase.PreConnect => "preConnect",
        Core.Models.Orchestration.SyncPhase.Pull => "pull",
        Core.Models.Orchestration.SyncPhase.Merge => "merge",
        Core.Models.Orchestration.SyncPhase.Push => "push",
        Core.Models.Orchestration.SyncPhase.WaitingForAppClose => "waitingForAppClose",
        Core.Models.Orchestration.SyncPhase.WaitingForDevice => "waitingForDevice",
        _ => "preConnect",
    };

    public static string? SyncPhase(string? trackerPhase) =>
        Enum.TryParse<SyncPhase>(trackerPhase, out var phase) ? SyncPhase(phase) : null;

    /// <summary>
    /// RunStatus has no value for "project folder unavailable": per the contract it's <c>skipped</c> with the
    /// <c>project_folder_unavailable</c> error code.
    /// </summary>
    public static string RunStatus(JobRunOutcome outcome) => outcome switch
    {
        JobRunOutcome.Completed => "completed",
        JobRunOutcome.CompletedNoChanges => "completedNoChanges",
        JobRunOutcome.Skipped => "skipped",
        JobRunOutcome.SkippedProjectFolderUnavailable => "skipped",
        JobRunOutcome.SkippedAppRunning => "skippedAppRunning",
        JobRunOutcome.Failed => "failed",
        JobRunOutcome.DryRunCompleted => "dryRunCompleted",
        JobRunOutcome.Cancelled => "cancelled",
        _ => "failed",
    };

    public static string? ErrorCode(RunErrorCode? code) => code switch
    {
        null => null,
        RunErrorCode.ValidationFailed => "validation_failed",
        RunErrorCode.ProjectFolderUnavailable => "project_folder_unavailable",
        RunErrorCode.PushSafetyBlocked => "push_safety_blocked",
        RunErrorCode.AppRunning => "app_running",
        RunErrorCode.AlreadyRunning => "already_running",
        RunErrorCode.DeviceUnreachable => "device_unreachable",
        _ => "internal_error",
    };
}
