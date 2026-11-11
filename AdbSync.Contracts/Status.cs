namespace AdbSync.Contracts;

public sealed record RunSummaryDto(Guid RunId, string Status, DateTimeOffset FinishedAt, string? ErrorCode, string? Message);

public sealed record CheckpointSummaryDto(DateTimeOffset SavedAt, string Phase, string Summary);

public sealed record DevicePhaseDto(Guid DeviceId, string Phase);

public sealed record StaleDeviceDto(Guid DeviceId, string Reason);

public sealed record AvailabilityDto(string ProjectFolder, string? Detail);

public sealed record JobStatusDto(
    Guid JobId,
    string Name,
    bool Enabled,
    string State,
    Guid? ActiveRunId,
    string? Phase,
    IReadOnlyList<DevicePhaseDto> Devices,
    RunSummaryDto? LastRun,
    DateTimeOffset? NextRunAt,
    CheckpointSummaryDto? Checkpoint,
    int PendingConflicts,
    string? AppPackage,
    IReadOnlyList<StaleDeviceDto> StaleDevices,
    Guid? BlockedByAppOn,
    bool CanForcePush,
    AvailabilityDto Availability,
    IReadOnlyList<WatchStateDto> Watch);

public sealed record SchedulerStateDto(bool Paused, DateTimeOffset? PausedAt);

public sealed record StatusSnapshotDto(long Seq, ServerInfo Server, SchedulerStateDto Scheduler, IReadOnlyList<JobStatusDto> Jobs);

public sealed record SchedulerPatch(bool? Paused);

public sealed record WatchStateDto(Guid DeviceId, string State, string? Detail, DateTimeOffset? Since);
