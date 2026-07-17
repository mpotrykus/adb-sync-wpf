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
    /// <summary>How many jobs can run at once. Independent of <see cref="MaxConcurrentPerDevice"/> - this is
    /// just a coarse cap on overall system load, so it can comfortably default above 1 now that jobs on
    /// different devices don't need to wait on each other.</summary>
    public int MaxConcurrentJobs { get; set; } = 4;

    /// <summary>How many transfers can run at once against the same physical device. WiFi sync transfers of
    /// many small files are latency-bound, not bandwidth-bound - a single connection spends most of its time
    /// waiting on round trips, so additional concurrent connections to the same device fill those idle gaps
    /// for a real throughput win. A concurrency sweep (1-8 jobs, same fixed workload, 3 trials each) showed most
    /// of the available win captured by 4 concurrent jobs, with returns flattening hard past that point and no
    /// transport errors observed at any level tested. Matches <see cref="MaxConcurrentJobs"/> since a single
    /// device can never see more concurrent jobs than that cap allows anyway.
    /// Sized once per device name on first use - restart to apply, like <see cref="MaxConcurrentJobs"/>.</summary>
    public int MaxConcurrentPerDevice { get; set; } = 4;

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

    /// <summary>When enabled, a job whose Interval/DailyAt schedule came due while AdbSync wasn't running fires
    /// once at the next opportunity instead of silently rolling forward to the next scheduled occurrence.</summary>
    public bool RunMissedSchedules { get; set; } = true;

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
