namespace AdbSync.Contracts;

public sealed record ConflictSideInfoDto(bool Deleted, long? Size, DateTimeOffset? ModifiedAt, bool HasPreview);

public sealed record ConflictDto(string Id, Guid JobId, Guid DeviceId, string Path, DateTimeOffset DetectedAt, ConflictSideInfoDto Master, ConflictSideInfoDto Device);

public sealed record ResolveRequest(string? Choice);

public sealed record ResolveManyRequest(string? Choice, Guid? JobId, IReadOnlyList<string>? Ids);

public sealed record ResolveFailure(string Id, string Code, string? Detail);

public sealed record ResolveResultDto(int Resolved, IReadOnlyList<ResolveFailure> Failed);
