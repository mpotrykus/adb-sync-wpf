using AdbSync.Core.Models.Config;
using AdbSync.Server.Api;

namespace AdbSync.Server.Tests;

public class JobMappingTests
{
    private static readonly DeviceConfig Phone = new() { Id = Guid.NewGuid(), Name = "Phone", Ip = "192.0.2.1" };

    private static AppConfig Config(params SyncJobConfig[] jobs) => new()
    {
        Settings = new GlobalSettings { ProjectsDirectory = Path.Combine(Path.GetTempPath(), "projects") },
        Devices = [Phone],
        Jobs = jobs.ToList(),
    };

    private static JobInputDto ValidInput(string name = "Photos") => new(
        name, null, "com.example.app", ["cache"], [new JobBindingDto(Phone.Id, "/sdcard/DCIM")],
        new ScheduleDto("dailyAt", 14400, ["07:30", "19:00"], 5, 600), true, false,
        new JobOverridesDto(null, null, null, null, null, null, "ask", null, 2, null, null, null, 30));

    [Fact]
    public void ApplyThenToDto_RoundTripsEveryField()
    {
        var config = Config();
        var job = new SyncJobConfig { Id = Guid.NewGuid() };
        config.Jobs.Add(job);

        JobMapping.Apply(ValidInput(), job, config);
        var input = JobMapping.ToInput(JobMapping.ToDto(job, config));

        Assert.Equivalent(ValidInput(), input);
        Assert.Equal("Phone", job.Devices[0].DeviceName);
        Assert.Equal(ConflictPolicy.Ask, job.ConflictPolicy);
    }

    [Fact]
    public void ETag_ChangesOnlyWhenTheJobDoes()
    {
        var config = Config();
        var job = new SyncJobConfig { Id = Guid.NewGuid() };
        config.Jobs.Add(job);
        JobMapping.Apply(ValidInput(), job, config);
        var before = JobMapping.ETag(JobMapping.ToDto(job, config));

        job.Schedule.LastRunAt = DateTimeOffset.UtcNow;
        Assert.Equal(before, JobMapping.ETag(JobMapping.ToDto(job, config)));

        job.Enabled = false;
        Assert.NotEqual(before, JobMapping.ETag(JobMapping.ToDto(job, config)));
    }

    [Fact]
    public void Validate_ValidInput_HasNoErrors() =>
        Assert.Empty(JobMapping.Validate(ValidInput(), Config(), null));

    [Fact]
    public void Validate_NameTakenIgnoringCase_IsAnErrorExceptForTheSameJob()
    {
        var existing = new SyncJobConfig { Id = Guid.NewGuid(), Name = "Photos" };
        var config = Config(existing);

        Assert.Contains("/name", JobMapping.Validate(ValidInput("photos"), config, null).Keys);
        Assert.Empty(JobMapping.Validate(ValidInput("photos"), config, existing.Id));
    }

    [Theory]
    [InlineData("sdcard/DCIM")]
    [InlineData("/sdcard/DCIM/")]
    [InlineData("")]
    public void Validate_RemotePathNotAbsoluteOrWithTrailingSlash_IsAnError(string remotePath)
    {
        var input = ValidInput() with { Devices = [new JobBindingDto(Phone.Id, remotePath)] };

        Assert.Contains("/devices/0/remotePath", JobMapping.Validate(input, Config(), null).Keys);
    }

    [Fact]
    public void Validate_UnknownDeviceRelativeFolderAndBadTimes_AreErrors()
    {
        var input = ValidInput() with
        {
            Devices = [new JobBindingDto(Guid.NewGuid(), "/sdcard")],
            ProjectDirectory = "relative/folder",
            Schedule = new ScheduleDto("dailyAt", 30, ["25:00"], -1, 10),
        };

        var errors = JobMapping.Validate(input, Config(), null);

        Assert.Contains("/devices/0/deviceId", errors.Keys);
        Assert.Contains("/projectDirectory", errors.Keys);
        Assert.Contains("/schedule/dailyTimes", errors.Keys);
        Assert.Contains("/schedule/intervalSeconds", errors.Keys);
        Assert.Contains("/schedule/debounceSeconds", errors.Keys);
        Assert.Contains("/schedule/rescanSeconds", errors.Keys);
    }
}
