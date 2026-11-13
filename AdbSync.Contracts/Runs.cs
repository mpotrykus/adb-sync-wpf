namespace AdbSync.Contracts;

public sealed record RunDto(
    Guid Id, Guid JobId, string JobName, string Status, string Mode, string Trigger, Guid? RequestedBy,
    DateTimeOffset QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? ErrorCode, string? Message,
    int FilesCopied, int FilesDeleted, int ErrorCount, long BytesCopied, double? PullSeconds, double? PushSeconds);

public sealed record RunStepDto(
    Guid DeviceId, string DeviceName, string Phase, string Status, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt,
    int FilesCopied, int FilesDeleted, long BytesCopied, int Errors, int Conflicts);

public sealed record RunDetailDto(RunDto Run, IReadOnlyList<RunStepDto> Steps);

public sealed record RunPageDto(IReadOnlyList<RunDto> Items, string? NextCursor);

public sealed record RunFileDto(Guid DeviceId, string? Phase, string Path, string Action, long? Size, string? Detail);

public sealed record RunFilesPageDto(bool Retained, IReadOnlyList<RunFileDto> Items, string? NextCursor);
