using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AdbSync.Server.Api;

public static class JobMapping
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<ScheduleKind, string> Kinds = new()
    {
        [ScheduleKind.Manual] = "manual",
        [ScheduleKind.Interval] = "interval",
        [ScheduleKind.DailyAt] = "dailyAt",
        [ScheduleKind.OnChange] = "onChange",
    };

    public static JobDto ToDto(SyncJobConfig job, AppConfig config)
    {
        var deviceIds = config.Devices.ToDictionary(d => d.Name, d => d.Id, StringComparer.OrdinalIgnoreCase);
        return new JobDto(
            job.Id,
            config.Jobs.IndexOf(job),
            ProjectFolders.TryProjectRoot(job.Resolve(config.Settings), job.Name) ?? "",
            job.Name,
            job.ProjectDirectory,
            job.AppPackage,
            job.Exclude,
            job.Devices.Select(b => new JobBindingDto(b.DeviceId != Guid.Empty ? b.DeviceId : deviceIds.GetValueOrDefault(b.DeviceName), b.RemotePath)).ToList(),
            new ScheduleDto(
                Kinds[job.Schedule.Kind],
                (int)job.Schedule.Interval.TotalSeconds,
                job.Schedule.DailyTimes.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture)).ToList(),
                (int)job.Schedule.DebounceWindow.TotalSeconds,
                (int)job.Schedule.RescanInterval.TotalSeconds),
            job.Enabled,
            job.DryRun,
            new JobOverridesDto(
                job.ConflictRetentionDays, job.ShowInfoNotifications, job.ShowErrorNotifications, job.MaxRunHistoryEntries,
                job.FileDetailRunsKept, job.BackupConflictLosers,
                job.ConflictPolicy switch { ConflictPolicy.Ask => "ask", ConflictPolicy.NewestWins => "newestWins", _ => null },
                job.CheckpointRetentionDays, job.RollbackPointsToKeep, job.BandwidthThrottleKBps, job.RetryMaxAttempts,
                job.RetryBackoffSeconds, job.PushSafetyMinimumPercent, job.IncrementalPull, job.FullPullIntervalDays));
    }

    public static JobInputDto ToInput(JobDto dto) => new(
        dto.Name, dto.ProjectDirectory, dto.AppPackage, dto.Exclude, dto.Devices, dto.Schedule, dto.Enabled, dto.DryRun, dto.Overrides);

    /// <summary>Changes whenever anything clients can see about the job changes.</summary>
    public static string ETag(JobDto dto) =>
        $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dto, Json))))[..16].ToLowerInvariant()}\"";

    /// <summary>Field errors keyed by JSON pointer, empty when the input is valid for <paramref name="existingId"/> (null for a new job).</summary>
    public static Dictionary<string, string[]> Validate(JobInputDto input, AppConfig config, Guid? existingId)
    {
        var errors = new Dictionary<string, string[]>();
        void Add(string pointer, string message) => errors[pointer] = [message];

        if (ConfigNames.Problem(input.Name) is { } nameProblem)
            Add("/name", nameProblem);
        else if (!ConfigNames.IsUnique(input.Name!, config.Jobs.Where(j => j.Id != existingId).Select(j => j.Name)))
            Add("/name", $"A job named '{input.Name}' already exists.");

        if (input.ProjectDirectory is { Length: > 0 } dir && !Path.IsPathFullyQualified(dir))
            Add("/projectDirectory", "Must be a full path on the server, or null to use the projects directory.");

        if (input.Devices is not { Count: > 0 })
            Add("/devices", "At least one device is required.");
        else
        {
            for (var i = 0; i < input.Devices.Count; i++)
            {
                var binding = input.Devices[i];
                if (config.Devices.All(d => d.Id != binding.DeviceId))
                    Add($"/devices/{i}/deviceId", "No device with that id.");
                if (string.IsNullOrWhiteSpace(binding.RemotePath) || !binding.RemotePath.StartsWith('/') || (binding.RemotePath.Length > 1 && binding.RemotePath.EndsWith('/')))
                    Add($"/devices/{i}/remotePath", "Must be an absolute device path with no trailing slash.");
            }
            if (input.Devices.Select(b => b.DeviceId).Distinct().Count() != input.Devices.Count)
                Add("/devices", "Each device can only be bound once.");
        }

        if (input.Schedule is { } schedule)
        {
            if (!Kinds.ContainsValue(schedule.Kind))
                Add("/schedule/kind", "Must be manual, interval, dailyAt or onChange.");
            if (schedule.IntervalSeconds is < 60)
                Add("/schedule/intervalSeconds", "Must be at least 60.");
            if (schedule.RescanSeconds is < 60)
                Add("/schedule/rescanSeconds", "Must be at least 60.");
            if (schedule.DebounceSeconds is < 0)
                Add("/schedule/debounceSeconds", "Must not be negative.");
            foreach (var time in schedule.DailyTimes ?? [])
            {
                if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    Add("/schedule/dailyTimes", $"'{time}' isn't a HH:mm time.");
            }
        }

        if (input.Overrides?.ConflictPolicy is { } policy && policy is not ("ask" or "newestWins"))
            Add("/overrides/conflictPolicy", "Must be newestWins, ask or null.");
        if (input.Overrides?.PushSafetyMinimumPercent is < 0 or > 100)
            Add("/overrides/pushSafetyMinimumPercent", "Must be between 0 and 100.");
        if (input.Overrides?.FullPullIntervalDays is < 0)
            Add("/overrides/fullPullIntervalDays", "Must not be negative.");

        return errors;
    }

    /// <summary>Copies a validated input onto <paramref name="job"/>, keeping its id and runtime state.</summary>
    public static void Apply(JobInputDto input, SyncJobConfig job, AppConfig config)
    {
        job.Name = input.Name!.Trim();
        job.ProjectDirectory = string.IsNullOrWhiteSpace(input.ProjectDirectory) ? null : input.ProjectDirectory;
        job.AppPackage = string.IsNullOrWhiteSpace(input.AppPackage) ? null : input.AppPackage.Trim();
        job.Exclude = (input.Exclude ?? []).ToList();
        job.Devices = input.Devices!.Select(b => new JobDeviceBinding
        {
            DeviceId = b.DeviceId,
            DeviceName = config.Devices.First(d => d.Id == b.DeviceId).Name,
            RemotePath = b.RemotePath,
        }).ToList();

        var schedule = input.Schedule ?? new ScheduleDto("manual", null, null, null, null);
        job.Schedule.Kind = Kinds.First(k => k.Value == schedule.Kind).Key;
        job.Schedule.Interval = TimeSpan.FromSeconds(schedule.IntervalSeconds ?? 14400);
        job.Schedule.DailyTimes = (schedule.DailyTimes ?? []).Select(t => TimeOnly.ParseExact(t, "HH:mm", CultureInfo.InvariantCulture)).ToList();
        job.Schedule.DebounceWindow = TimeSpan.FromSeconds(schedule.DebounceSeconds ?? 10);
        job.Schedule.RescanInterval = TimeSpan.FromSeconds(schedule.RescanSeconds ?? 900);

        job.Enabled = input.Enabled ?? true;
        job.DryRun = input.DryRun ?? false;

        var o = input.Overrides;
        job.ConflictRetentionDays = o?.ConflictRetentionDays;
        job.ShowInfoNotifications = o?.ShowInfoNotifications;
        job.ShowErrorNotifications = o?.ShowErrorNotifications;
        job.MaxRunHistoryEntries = o?.MaxRunHistoryEntries;
        job.FileDetailRunsKept = o?.FileDetailRunsKept;
        job.BackupConflictLosers = o?.BackupConflictLosers;
        job.ConflictPolicy = o?.ConflictPolicy switch { "ask" => ConflictPolicy.Ask, "newestWins" => ConflictPolicy.NewestWins, _ => null };
        job.CheckpointRetentionDays = o?.CheckpointRetentionDays;
        job.RollbackPointsToKeep = o?.RollbackPointsToKeep;
        job.BandwidthThrottleKBps = o?.BandwidthThrottleKBps;
        job.RetryMaxAttempts = o?.RetryMaxAttempts;
        job.RetryBackoffSeconds = o?.RetryBackoffSeconds;
        job.PushSafetyMinimumPercent = o?.PushSafetyMinimumPercent;
        job.IncrementalPull = o?.IncrementalPull;
        job.FullPullIntervalDays = o?.FullPullIntervalDays;
    }
}
