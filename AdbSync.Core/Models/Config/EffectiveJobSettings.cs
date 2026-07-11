using AdbSync.Core.Models.Transfer;

namespace AdbSync.Core.Models.Config;

/// <summary>Every setting that can be either job-overridden or inherited from <see cref="GlobalSettings"/>, already resolved. Build via <see cref="SyncJobConfigExtensions.Resolve"/>.</summary>
public sealed record EffectiveJobSettings(
    string ProjectsDirectory,
    int StaleLockHours,
    int ConflictRetentionDays,
    bool ShowInfoNotifications,
    bool ShowErrorNotifications,
    int MaxRunHistoryEntries,
    bool BackupConflictLosers,
    bool DryRun,
    int CheckpointRetentionDays,
    int? BandwidthThrottleKBps,
    int RetryMaxAttempts,
    TimeSpan RetryBackoff,
    int PushSafetyMinimumPercent)
{
    public TransferPolicy ToTransferPolicy() => new(RetryMaxAttempts, RetryBackoff, BandwidthThrottleKBps);
}

public static class SyncJobConfigExtensions
{
    /// <summary>Resolves every overridable setting for this job, falling back to <paramref name="global"/> wherever the job leaves it unset.</summary>
    public static EffectiveJobSettings Resolve(this SyncJobConfig job, GlobalSettings global) => new(
        ProjectsDirectory: string.IsNullOrWhiteSpace(job.ProjectDirectory) ? global.ProjectsDirectory : job.ProjectDirectory,
        StaleLockHours: job.StaleLockHours ?? global.StaleLockHours,
        ConflictRetentionDays: job.ConflictRetentionDays ?? global.ConflictRetentionDays,
        ShowInfoNotifications: job.ShowInfoNotifications ?? global.ShowInfoNotifications,
        ShowErrorNotifications: job.ShowErrorNotifications ?? global.ShowErrorNotifications,
        MaxRunHistoryEntries: job.MaxRunHistoryEntries ?? global.MaxRunHistoryEntries,
        BackupConflictLosers: job.BackupConflictLosers ?? global.BackupConflictLosers,
        DryRun: job.DryRun,
        CheckpointRetentionDays: job.CheckpointRetentionDays ?? global.CheckpointRetentionDays,
        BandwidthThrottleKBps: job.BandwidthThrottleKBps ?? global.BandwidthThrottleKBps,
        RetryMaxAttempts: job.RetryMaxAttempts ?? global.RetryMaxAttempts,
        RetryBackoff: TimeSpan.FromSeconds(job.RetryBackoffSeconds ?? global.RetryBackoffSeconds),
        PushSafetyMinimumPercent: job.PushSafetyMinimumPercent ?? global.PushSafetyMinimumPercent);
}
