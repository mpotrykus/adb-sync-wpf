using AdbSync.Core.Services.Scheduling;

namespace AdbSync.Core.Models.Config;

public sealed class GlobalSettings
{
    public string ProjectsDirectory { get; set; } = "";
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
    /// <summary>IANA time zone (for example America/Chicago) for daily schedules and quiet hours. Empty means the host's.</summary>
    public string? TimeZone { get; set; }
    public bool QuietHoursEnabled { get; set; }
    public TimeOnly QuietHoursStart { get; set; } = new(22, 0);
    public TimeOnly QuietHoursEnd { get; set; } = new(7, 0);

    /// <summary>Default retention for job checkpoints/snapshots, unless a job overrides it. Mirrors <see cref="ConflictRetentionDays"/>.</summary>
    public int CheckpointRetentionDays { get; set; } = 30;

    /// <summary>Default number of automatic rollback points kept per job, unless a job overrides it. Each sync that
    /// changes something first copies master plus every device's pulled folder, so this multiplies the project's
    /// disk footprint. 0 turns automatic rollback points off.</summary>
    public int RollbackPointsToKeep { get; set; } = 3;

    /// <summary>Default cap on adb transfer throughput, unless a job overrides it. Null/unset = unlimited.</summary>
    public int? BandwidthThrottleKBps { get; set; }

    /// <summary>Default number of attempts for a single adb file transfer before it's reported as failed.</summary>
    public int RetryMaxAttempts { get; set; } = 3;

    /// <summary>Default delay between adb transfer retry attempts.</summary>
    public int RetryBackoffSeconds { get; set; } = 5;

    /// <summary>Default minimum percent of the historical baseline file count master must retain to allow a push.</summary>
    public int PushSafetyMinimumPercent { get; set; } = 25;

    /// <summary>Default for incremental pull: only transfer device files whose size/mtime differ from what the last
    /// successful sync recorded, instead of re-pulling the whole folder every run. Dry runs always pull everything.</summary>
    public bool IncrementalPull { get; set; }

    /// <summary>How many days may pass before an incremental-pull job does one full pull anyway, as a safety net
    /// against a wrong skip decision. 0 means every run is a full pull.</summary>
    public int FullPullIntervalDays { get; set; } = 7;

    /// <summary>Default number of run-history rows kept per job, unless a job overrides it.</summary>
    public int MaxRunHistoryEntries { get; set; } = 50;

    /// <summary>Default number of most-recent runs per job whose per-file change list is kept; older runs keep their totals and
    /// log but not their file lists. 0 keeps every run's file list, which lets the history database grow without bound.</summary>
    public int FileDetailRunsKept { get; set; } = 10;

    /// <summary>Default for how a file changed on two sides at once is resolved, unless a job overrides it.</summary>
    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.NewestWins;

    /// <summary>Default for whether a losing side of a merge conflict is backed up before being overwritten.</summary>
    public bool BackupConflictLosers { get; set; } = true;

    /// <summary>When enabled, a job whose Interval/DailyAt schedule came due while AdbSync wasn't running fires
    /// once at the next opportunity instead of silently rolling forward to the next scheduled occurrence.</summary>
    public bool RunMissedSchedules { get; set; } = true;

    /// <summary>Sends job progress to the AdbSync Companion Android app (via <c>adb shell am broadcast</c>) on
    /// devices that are already connected. Harmless on devices without the app installed.</summary>
    public bool CompanionBroadcastsEnabled { get; set; } = true;

    /// <summary>Serves job status and run/stop actions to the companion app over the LAN (HTTP + SSE). The
    /// phone learns the address and <see cref="CompanionServerToken"/> automatically over adb. Restart to apply.</summary>
    public bool CompanionServerEnabled { get; set; } = true;

    public int CompanionServerPort { get; set; } = 47615;

    /// <summary>Bearer token the companion app must send. Generated on first server start when empty.</summary>
    public string? CompanionServerToken { get; set; }

    /// <summary>True if the given time falls within the configured quiet hours window.</summary>
    public bool IsQuietNow(TimeOnly? nowOverride = null)
    {
        if (!QuietHoursEnabled)
            return false;

        var now = nowOverride ?? TimeOnly.FromDateTime(ScheduleClock.Now(this).DateTime);
        return QuietHoursStart <= QuietHoursEnd
            ? now >= QuietHoursStart && now < QuietHoursEnd
            : now >= QuietHoursStart || now < QuietHoursEnd;
    }
}
