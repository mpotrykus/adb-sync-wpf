using AdbSync.Core.Config;
using AdbSync.Core.Scheduling;

namespace AdbSync.Core.Tests.Scheduling;

public class ScheduleCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero); // a Monday, noon

    [Fact]
    public void Manual_IsNeverDue()
    {
        var schedule = new JobSchedule { Kind = ScheduleKind.Manual };

        Assert.Null(ScheduleCalculator.NextDueUtc(schedule, Now));
    }

    [Fact]
    public void Interval_NeverRunBefore_IsDueImmediately()
    {
        var schedule = new JobSchedule { Kind = ScheduleKind.Interval, Interval = TimeSpan.FromHours(4), LastRunAt = null };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now);

        Assert.True(due <= Now);
    }

    [Fact]
    public void Interval_WithLastRunAt_IsLastRunPlusInterval()
    {
        var lastRun = Now.AddHours(-2);
        var schedule = new JobSchedule { Kind = ScheduleKind.Interval, Interval = TimeSpan.FromHours(4), LastRunAt = lastRun };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now);

        Assert.Equal(lastRun.AddHours(4), due);
    }

    [Fact]
    public void DailyAt_EmptyList_IsNeverDue()
    {
        var schedule = new JobSchedule { Kind = ScheduleKind.DailyAt, DailyTimes = [] };

        Assert.Null(ScheduleCalculator.NextDueUtc(schedule, Now));
    }

    [Fact]
    public void DailyAt_TimeLaterToday_ReturnsTodayOccurrence()
    {
        var schedule = new JobSchedule { Kind = ScheduleKind.DailyAt, DailyTimes = [new TimeOnly(18, 0)] };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now);

        Assert.Equal(new DateTimeOffset(2026, 6, 15, 18, 0, 0, TimeSpan.Zero), due);
    }

    [Fact]
    public void DailyAt_TimeAlreadyPassedToday_RollsOverToTomorrow()
    {
        var schedule = new JobSchedule { Kind = ScheduleKind.DailyAt, DailyTimes = [new TimeOnly(6, 0)] };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now);

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

        var due = ScheduleCalculator.NextDueUtc(schedule, Now);

        Assert.Equal(new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero), due);
    }

    [Fact]
    public void DailyAt_ExactlyNow_IsDueNow()
    {
        var schedule = new JobSchedule { Kind = ScheduleKind.DailyAt, DailyTimes = [new TimeOnly(12, 0)] };

        var due = ScheduleCalculator.NextDueUtc(schedule, Now);

        Assert.Equal(Now, due);
    }
}
