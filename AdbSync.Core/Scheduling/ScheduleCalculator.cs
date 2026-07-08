using AdbSync.Core.Config;

namespace AdbSync.Core.Scheduling;

/// <summary>
/// Pure schedule-due-time math, deliberately free of any DI/IO so schedule bugs ("runs every 4 hours" silently
/// breaking) are catchable with plain unit tests. Callers decide the time frame: pass local DateTimeOffset.Now
/// for "daily at HH:mm" schedules to mean local wall-clock time, as a user configuring one would expect.
/// </summary>
public static class ScheduleCalculator
{
    public static DateTimeOffset? NextDueUtc(JobSchedule schedule, DateTimeOffset now) => schedule.Kind switch
    {
        ScheduleKind.Manual => null,
        ScheduleKind.Interval => (schedule.LastRunAt ?? DateTimeOffset.MinValue) + schedule.Interval,
        ScheduleKind.DailyAt => NextDailyOccurrence(schedule.DailyTimes, now),
        ScheduleKind.OnChange => null, // never "due" via polling - the change watcher triggers runs directly
        _ => null,
    };

    private static DateTimeOffset? NextDailyOccurrence(IReadOnlyList<TimeOnly> times, DateTimeOffset now)
    {
        if (times.Count == 0)
            return null;

        var today = DateOnly.FromDateTime(now.DateTime);
        return times.Select(t => NextOccurrenceOf(t, today, now)).Min();
    }

    private static DateTimeOffset NextOccurrenceOf(TimeOnly time, DateOnly today, DateTimeOffset now)
    {
        var todayOccurrence = ToDateTimeOffset(today, time, now.Offset);
        return todayOccurrence >= now ? todayOccurrence : ToDateTimeOffset(today.AddDays(1), time, now.Offset);
    }

    private static DateTimeOffset ToDateTimeOffset(DateOnly date, TimeOnly time, TimeSpan offset) =>
        new(date.Year, date.Month, date.Day, time.Hour, time.Minute, time.Second, offset);
}
