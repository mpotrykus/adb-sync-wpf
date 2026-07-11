namespace AdbSync.Core.Models.Config;

public sealed class GlobalSettings
{
    public static string DefaultProjectsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AdbSync Projects");

    public string ProjectsDirectory { get; set; } = DefaultProjectsDirectory;
    public bool StartAtLogin { get; set; } = true;
    public bool ShowInfoNotifications { get; set; }
    public bool ShowErrorNotifications { get; set; } = true;
    public int StaleLockHours { get; set; } = 4;
    public int LogRetentionDays { get; set; } = 30;
    public int ConflictRetentionDays { get; set; } = 30;
    public long PerLogFileMaxBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>When enabled, tray notifications are suppressed during the [Start, End) window (wraps past midnight if Start &gt; End).</summary>
    public bool QuietHoursEnabled { get; set; }
    public TimeOnly QuietHoursStart { get; set; } = new(22, 0);
    public TimeOnly QuietHoursEnd { get; set; } = new(7, 0);

    /// <summary>Default retention for job checkpoints/snapshots, unless a job overrides it. Mirrors <see cref="ConflictRetentionDays"/>.</summary>
    public int CheckpointRetentionDays { get; set; } = 30;

    /// <summary>Default cap on adb transfer throughput, unless a job overrides it. Null/unset = unlimited.</summary>
    public int? BandwidthThrottleKBps { get; set; }

    /// <summary>Default number of attempts for a single adb file transfer before it's reported as failed.</summary>
    public int RetryMaxAttempts { get; set; } = 3;

    /// <summary>Default delay between adb transfer retry attempts.</summary>
    public int RetryBackoffSeconds { get; set; } = 5;

    /// <summary>Default minimum percent of the historical baseline file count master must retain to allow a push.</summary>
    public int PushSafetyMinimumPercent { get; set; } = 25;

    /// <summary>Default number of run-history rows kept per job, unless a job overrides it.</summary>
    public int MaxRunHistoryEntries { get; set; } = 50;

    /// <summary>Default for whether a losing side of a merge conflict is backed up before being overwritten.</summary>
    public bool BackupConflictLosers { get; set; } = true;

    /// <summary>True if the given time falls within the configured quiet hours window.</summary>
    public bool IsQuietNow(TimeOnly? nowOverride = null)
    {
        if (!QuietHoursEnabled)
            return false;

        var now = nowOverride ?? TimeOnly.FromDateTime(DateTime.Now);
        return QuietHoursStart <= QuietHoursEnd
            ? now >= QuietHoursStart && now < QuietHoursEnd
            : now >= QuietHoursStart || now < QuietHoursEnd;
    }
}
