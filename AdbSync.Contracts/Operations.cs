namespace AdbSync.Contracts;

public sealed record OperationProgressDto(string Message, double? Fraction);

public sealed record OperationDto(
    string Id, string Kind, string Status, Guid? JobId, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt,
    OperationProgressDto? Progress, object? Result, string? ErrorCode, string? Message);
