namespace AdbSync.Core.Models.Config;

public enum ScheduleKind
{
    /// <summary>Never auto-runs; only triggered via "Run Now".</summary>
    Manual,

    /// <summary>Runs every <see cref="JobSchedule.Interval"/>, measured from the previous run's start.</summary>
    Interval,

    /// <summary>Runs once at each time of day listed in <see cref="JobSchedule.DailyTimes"/>.</summary>
    DailyAt,

    /// <summary>Runs whenever a bound device folder changes, detected by a live watcher rather than polling.</summary>
    OnChange,
}

public sealed class JobSchedule
{
    public ScheduleKind Kind { get; set; } = ScheduleKind.Manual;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(4);
    public List<TimeOnly> DailyTimes { get; set; } = [];

    /// <summary>Quiet period after the last detected change before an OnChange job actually runs, so a burst of writes triggers one run instead of many.</summary>
    public TimeSpan DebounceWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often an OnChange watcher re-enumerates subdirectories to pick up newly created folders (the underlying device-side watch isn't recursive).</summary>
    public TimeSpan RescanInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Stamped at the start of every run (scheduled or manual) so interval math doesn't double-fire.</summary>
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
}
