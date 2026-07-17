using AdbSync.Core.Models.Config;

namespace AdbSync.Core.Services.Scheduling;

/// <summary>
/// Pure schedule-due-time math, deliberately free of any DI/IO so schedule bugs ("runs every 4 hours" silently
/// breaking) are catchable with plain unit tests. Callers decide the time frame: pass local DateTimeOffset.Now
/// for "daily at HH:mm" schedules to mean local wall-clock time, as a user configuring one would expect.
/// </summary>
public static class ScheduleCalculator
{
    /// <summary>
    /// <paramref name="runMissedSchedules"/> mirrors <see cref="GlobalSettings.RunMissedSchedules"/>: when true, a
    /// schedule that came due while nothing was polling it (app closed) is still returned as due rather than
    /// silently rolling forward to the next occurrence. Only applies to schedules that have run at least once
    /// (<see cref="JobSchedule.LastRunAt"/> not null) - a brand-new job doesn't "catch up" the moment it's created.
    /// </summary>
    public static DateTimeOffset? NextDueUtc(JobSchedule schedule, DateTimeOffset now, bool runMissedSchedules) => schedule.Kind switch
    {
        ScheduleKind.Manual => null,
        ScheduleKind.Interval => NextIntervalDue(schedule, now, runMissedSchedules),
        ScheduleKind.DailyAt => NextDailyOccurrence(schedule.DailyTimes, now, schedule.LastRunAt, runMissedSchedules),
        ScheduleKind.OnChange => null,
        _ => null,
    };

    private static DateTimeOffset NextIntervalDue(JobSchedule schedule, DateTimeOffset now, bool runMissedSchedules)
    {
        var due = (schedule.LastRunAt ?? DateTimeOffset.MinValue) + schedule.Interval;
        if (runMissedSchedules || due > now || schedule.Interval <= TimeSpan.Zero)
            return due;

        var missedCycles = (long)((now - due) / schedule.Interval) + 1;
        return due + TimeSpan.FromTicks(schedule.Interval.Ticks * missedCycles);
    }

    private static DateTimeOffset? NextDailyOccurrence(
        IReadOnlyList<TimeOnly> times, DateTimeOffset now, DateTimeOffset? lastRunAt, bool runMissedSchedules)
    {
        if (times.Count == 0)
            return null;

        if (runMissedSchedules)
        {
            var missed = MostRecentMissedOccurrence(times, now, lastRunAt);
            if (missed is not null)
                return missed;
        }

        var today = DateOnly.FromDateTime(now.DateTime);
        return times.Select(t => NextOccurrenceOf(t, today, now)).Min();
    }

    /// <summary>The most recent configured time-of-day that already passed (today or yesterday - a daily time
    /// never goes stale for longer than that) but hasn't been run yet, if any.</summary>
    private static DateTimeOffset? MostRecentMissedOccurrence(
        IReadOnlyList<TimeOnly> times, DateTimeOffset now, DateTimeOffset? lastRunAt)
    {
        if (lastRunAt is null)
            return null;

        var today = DateOnly.FromDateTime(now.DateTime);
        var yesterday = today.AddDays(-1);

        var missed = times
            .SelectMany(t => new[] { ToDateTimeOffset(yesterday, t, now.Offset), ToDateTimeOffset(today, t, now.Offset) })
            .Where(occurrence => occurrence <= now && occurrence > lastRunAt)
            .ToList();

        return missed.Count == 0 ? null : missed.Max();
    }

    private static DateTimeOffset NextOccurrenceOf(TimeOnly time, DateOnly today, DateTimeOffset now)
    {
        var todayOccurrence = ToDateTimeOffset(today, time, now.Offset);
        return todayOccurrence >= now ? todayOccurrence : ToDateTimeOffset(today.AddDays(1), time, now.Offset);
    }

    private static DateTimeOffset ToDateTimeOffset(DateOnly date, TimeOnly time, TimeSpan offset) =>
        new(date.Year, date.Month, date.Day, time.Hour, time.Minute, time.Second, offset);
}
