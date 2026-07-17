using AdbSync.Core.Models.Config;

namespace AdbSync.Core.Tests.Config;

public class EffectiveJobSettingsTests
{
    private static readonly GlobalSettings Global = new()
    {
        ProjectsDirectory = @"C:\global\projects",
        StaleLockHours = 4,
        ConflictRetentionDays = 30,
        ShowInfoNotifications = false,
        ShowErrorNotifications = true,
        MaxRunHistoryEntries = 50,
        BackupConflictLosers = true,
        CheckpointRetentionDays = 30,
        BandwidthThrottleKBps = null,
        RetryMaxAttempts = 3,
        RetryBackoffSeconds = 5,
        PushSafetyMinimumPercent = 25,
    };

    [Fact]
    public void Resolve_JobLeavesEverythingUnset_InheritsAllGlobalDefaults()
    {
        var job = new SyncJobConfig { Name = "Job" };

        var eff = job.Resolve(Global);

        Assert.Equal(Global.ProjectsDirectory, eff.ProjectsDirectory);
        Assert.Equal(Global.StaleLockHours, eff.StaleLockHours);
        Assert.Equal(Global.ConflictRetentionDays, eff.ConflictRetentionDays);
        Assert.Equal(Global.ShowInfoNotifications, eff.ShowInfoNotifications);
        Assert.Equal(Global.ShowErrorNotifications, eff.ShowErrorNotifications);
        Assert.Equal(Global.MaxRunHistoryEntries, eff.MaxRunHistoryEntries);
        Assert.Equal(Global.BackupConflictLosers, eff.BackupConflictLosers);
        Assert.Equal(Global.CheckpointRetentionDays, eff.CheckpointRetentionDays);
        Assert.Null(eff.BandwidthThrottleKBps);
        Assert.Equal(Global.RetryMaxAttempts, eff.RetryMaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(Global.RetryBackoffSeconds), eff.RetryBackoff);
        Assert.Equal(Global.PushSafetyMinimumPercent, eff.PushSafetyMinimumPercent);
        Assert.False(eff.DryRun);
    }

    [Fact]
    public void Resolve_JobOverridesEverything_UsesJobValuesInstead()
    {
        var job = new SyncJobConfig
        {
            Name = "Job",
            ProjectDirectory = @"C:\job\projects",
            StaleLockHours = 1,
            ConflictRetentionDays = 7,
            ShowInfoNotifications = true,
            ShowErrorNotifications = false,
            MaxRunHistoryEntries = 5,
            BackupConflictLosers = false,
            CheckpointRetentionDays = 3,
            BandwidthThrottleKBps = 512,
            RetryMaxAttempts = 1,
            RetryBackoffSeconds = 0,
            PushSafetyMinimumPercent = 10,
            DryRun = true,
        };

        var eff = job.Resolve(Global);

        Assert.Equal(job.ProjectDirectory, eff.ProjectsDirectory);
        Assert.Equal(1, eff.StaleLockHours);
        Assert.Equal(7, eff.ConflictRetentionDays);
        Assert.True(eff.ShowInfoNotifications);
        Assert.False(eff.ShowErrorNotifications);
        Assert.Equal(5, eff.MaxRunHistoryEntries);
        Assert.False(eff.BackupConflictLosers);
        Assert.Equal(3, eff.CheckpointRetentionDays);
        Assert.Equal(512, eff.BandwidthThrottleKBps);
        Assert.Equal(1, eff.RetryMaxAttempts);
        Assert.Equal(TimeSpan.Zero, eff.RetryBackoff);
        Assert.Equal(10, eff.PushSafetyMinimumPercent);
        Assert.True(eff.DryRun);
    }

    [Fact]
    public void Resolve_BlankProjectDirectoryOverride_TreatedAsUnsetAndFallsBackToGlobal()
    {
        var job = new SyncJobConfig { Name = "Job", ProjectDirectory = "   " };

        var eff = job.Resolve(Global);

        Assert.Equal(Global.ProjectsDirectory, eff.ProjectsDirectory);
    }

    [Theory]
    [InlineData("21:59", false)]
    [InlineData("22:00", true)]
    [InlineData("23:30", true)]
    [InlineData("03:00", true)]
    [InlineData("06:59", true)]
    [InlineData("07:00", false)]
    [InlineData("12:00", false)]
    public void IsQuietNow_WithWraparoundWindow_ReturnsExpected(string time, bool expectedQuiet)
    {
        var settings = new GlobalSettings
        {
            QuietHoursEnabled = true,
            QuietHoursStart = new TimeOnly(22, 0),
            QuietHoursEnd = new TimeOnly(7, 0),
        };

        Assert.Equal(expectedQuiet, settings.IsQuietNow(TimeOnly.Parse(time)));
    }

    [Fact]
    public void IsQuietNow_Disabled_AlwaysFalseRegardlessOfTime()
    {
        var settings = new GlobalSettings
        {
            QuietHoursEnabled = false,
            QuietHoursStart = new TimeOnly(22, 0),
            QuietHoursEnd = new TimeOnly(7, 0),
        };

        Assert.False(settings.IsQuietNow(new TimeOnly(23, 0)));
    }

    [Fact]
    public void IsQuietNow_NonWrappingWindow_OnlyQuietBetweenStartAndEnd()
    {
        var settings = new GlobalSettings
        {
            QuietHoursEnabled = true,
            QuietHoursStart = new TimeOnly(9, 0),
            QuietHoursEnd = new TimeOnly(17, 0),
        };

        Assert.True(settings.IsQuietNow(new TimeOnly(12, 0)));
        Assert.False(settings.IsQuietNow(new TimeOnly(20, 0)));
    }
}
