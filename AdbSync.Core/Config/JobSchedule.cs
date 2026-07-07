namespace AdbSync.Core.Config;

public enum ScheduleKind
{
    /// <summary>Never auto-runs; only triggered via "Run Now".</summary>
    Manual,

    /// <summary>Runs every <see cref="JobSchedule.Interval"/>, measured from the previous run's start.</summary>
    Interval,

    /// <summary>Runs once at each time of day listed in <see cref="JobSchedule.DailyTimes"/>.</summary>
    DailyAt,
}

public sealed class JobSchedule
{
    public ScheduleKind Kind { get; set; } = ScheduleKind.Manual;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(4);
    public List<TimeOnly> DailyTimes { get; set; } = [];

    /// <summary>Stamped at the start of every run (scheduled or manual) so interval math doesn't double-fire.</summary>
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
}
