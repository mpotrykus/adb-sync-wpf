using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Scheduling;

namespace AdbSync.Core.Tests.Scheduling;

public class ScheduleCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Manual_IsNeverDue()
    {
        var schedule = new JobSchedule { Kind = ScheduleKind.Manual };

        Assert.Null(ScheduleCalculator.NextDueUtc(schedule, Now, runMissedSchedules: true));
    }

    [Fact]
    public void Interval_NeverRunBefore_IsDueImmediately()
    {
        var schedule = new JobSchedule { Kind = ScheduleKind.Interval, Interval = TimeSpan.FromHours(4), LastRunAt = null };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now, runMissedSchedules: true);

        Assert.True(due <= Now);
    }

    [Fact]
    public void Interval_WithLastRunAt_IsLastRunPlusInterval()
    {
        var lastRun = Now.AddHours(-2);
        var schedule = new JobSchedule { Kind = ScheduleKind.Interval, Interval = TimeSpan.FromHours(4), LastRunAt = lastRun };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now, runMissedSchedules: true);

        Assert.Equal(lastRun.AddHours(4), due);
    }

    [Fact]
    public void DailyAt_EmptyList_IsNeverDue()
    {
        var schedule = new JobSchedule { Kind = ScheduleKind.DailyAt, DailyTimes = [] };

        Assert.Null(ScheduleCalculator.NextDueUtc(schedule, Now, runMissedSchedules: true));
    }

    [Fact]
    public void DailyAt_TimeLaterToday_ReturnsTodayOccurrence()
    {
        var schedule = new JobSchedule { Kind = ScheduleKind.DailyAt, DailyTimes = [new TimeOnly(18, 0)] };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now, runMissedSchedules: true);

        Assert.Equal(new DateTimeOffset(2026, 6, 15, 18, 0, 0, TimeSpan.Zero), due);
    }

    [Fact]
    public void DailyAt_TimeAlreadyPassedToday_RollsOverToTomorrow()
    {
        var schedule = new JobSchedule { Kind = ScheduleKind.DailyAt, DailyTimes = [new TimeOnly(6, 0)] };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now, runMissedSchedules: true);

        Assert.Equal(new DateTimeOffset(2026, 6, 16, 6, 0, 0, TimeSpan.Zero), due);
    }

    [Fact]
    public void DailyAt_MultipleTimes_PicksEarliestUpcoming()
    {
        var schedule = new JobSchedule
        {
            Kind = ScheduleKind.DailyAt,
            DailyTimes = [new TimeOnly(6, 0), new TimeOnly(14, 0), new TimeOnly(20, 0)],
        };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now, runMissedSchedules: true);

        Assert.Equal(new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero), due);
    }

    [Fact]
    public void DailyAt_ExactlyNow_IsDueNow()
    {
        var schedule = new JobSchedule { Kind = ScheduleKind.DailyAt, DailyTimes = [new TimeOnly(12, 0)] };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now, runMissedSchedules: true);

        Assert.Equal(Now, due);
    }

    [Fact]
    public void OnChange_IsNeverDueViaPolling()
    {
        var schedule = new JobSchedule { Kind = ScheduleKind.OnChange };

        Assert.Null(ScheduleCalculator.NextDueUtc(schedule, Now, runMissedSchedules: true));
    }

    [Fact]
    public void DailyAt_MissedWhileAppWasClosed_CatchesUpImmediately()
    {
        var schedule = new JobSchedule
        {
            Kind = ScheduleKind.DailyAt,
            DailyTimes = [new TimeOnly(6, 0)],
            LastRunAt = Now.AddDays(-1).AddHours(-1),
        };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now, runMissedSchedules: true);

        Assert.Equal(new DateTimeOffset(2026, 6, 15, 6, 0, 0, TimeSpan.Zero), due);
    }

    [Fact]
    public void DailyAt_MissedWhileAppWasClosed_CatchUpDisabled_RollsOverToTomorrow()
    {
        var schedule = new JobSchedule
        {
            Kind = ScheduleKind.DailyAt,
            DailyTimes = [new TimeOnly(6, 0)],
            LastRunAt = Now.AddDays(-1).AddHours(-1),
        };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now, runMissedSchedules: false);

        Assert.Equal(new DateTimeOffset(2026, 6, 16, 6, 0, 0, TimeSpan.Zero), due);
    }

    [Fact]
    public void DailyAt_NeverRunBefore_DoesNotCatchUpOnCreation()
    {
        var schedule = new JobSchedule { Kind = ScheduleKind.DailyAt, DailyTimes = [new TimeOnly(6, 0)], LastRunAt = null };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now, runMissedSchedules: true);

        Assert.Equal(new DateTimeOffset(2026, 6, 16, 6, 0, 0, TimeSpan.Zero), due);
    }

    [Fact]
    public void DailyAt_AlreadyRunSinceMissedTime_DoesNotRerun()
    {
        var schedule = new JobSchedule
        {
            Kind = ScheduleKind.DailyAt,
            DailyTimes = [new TimeOnly(6, 0)],
            LastRunAt = new DateTimeOffset(2026, 6, 15, 6, 0, 0, TimeSpan.Zero),
        };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now, runMissedSchedules: true);

        Assert.Equal(new DateTimeOffset(2026, 6, 16, 6, 0, 0, TimeSpan.Zero), due);
    }

    [Fact]
    public void Interval_MissedWhileAppWasClosed_CatchUpDisabled_ResyncsToGridInsteadOfFiringImmediately()
    {
        var schedule = new JobSchedule
        {
            Kind = ScheduleKind.Interval,
            Interval = TimeSpan.FromHours(4),
            LastRunAt = Now.AddHours(-10),
        };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now, runMissedSchedules: false);

        Assert.Equal(Now.AddHours(2), due);
    }

    [Fact]
    public void Interval_Missed_CatchUpEnabled_IsDueImmediately()
    {
        var schedule = new JobSchedule
        {
            Kind = ScheduleKind.Interval,
            Interval = TimeSpan.FromHours(4),
            LastRunAt = Now.AddHours(-10),
        };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now, runMissedSchedules: true);

        Assert.True(due <= Now);
    }
}
