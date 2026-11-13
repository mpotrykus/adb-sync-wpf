namespace AdbSync.Contracts;

public sealed record JobBindingDto(Guid DeviceId, string RemotePath);

public sealed record ScheduleDto(string Kind, int? IntervalSeconds, IReadOnlyList<string>? DailyTimes, int? DebounceSeconds, int? RescanSeconds);

public sealed record JobOverridesDto(
    int? ConflictRetentionDays, bool? ShowInfoNotifications, bool? ShowErrorNotifications, int? MaxRunHistoryEntries,
    int? FileDetailRunsKept, bool? BackupConflictLosers, string? ConflictPolicy, int? CheckpointRetentionDays,
    int? RollbackPointsToKeep, int? BandwidthThrottleKBps, int? RetryMaxAttempts, int? RetryBackoffSeconds,
    int? PushSafetyMinimumPercent, bool? IncrementalPull = null, int? FullPullIntervalDays = null);

/// <summary>JobInput: what clients send to create a job, and what a PATCH is merged into.</summary>
public sealed record JobInputDto(
    string? Name, string? ProjectDirectory, string? AppPackage, IReadOnlyList<string>? Exclude, IReadOnlyList<JobBindingDto>? Devices,
    ScheduleDto? Schedule, bool? Enabled, bool? DryRun, JobOverridesDto? Overrides);

public sealed record JobDto(
    Guid Id, int Order, string ProjectPath, string Name, string? ProjectDirectory, string? AppPackage, IReadOnlyList<string> Exclude,
    IReadOnlyList<JobBindingDto> Devices, ScheduleDto Schedule, bool Enabled, bool DryRun, JobOverridesDto Overrides);

public sealed record SnapshotDto(
    string Id, Guid JobId, string Kind, DateTimeOffset CreatedAt, IReadOnlyList<Guid> DeviceIds, bool IncludesMaster, long? SizeBytes, string Path);

public sealed record ManifestSummaryDto(Guid DeviceId, int Entries, DateTimeOffset? LastMergedAt, string? MasterPath);

public sealed record WatchAvailabilityDto(Guid DeviceId, bool LiveWatchSupported, string Detail, string? Warning);

public sealed record DeviceRefDto(Guid DeviceId);
