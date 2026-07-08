using AdbSync.Core.Orchestration;

namespace AdbSync.Core.Orchestration.RunHistory;

public sealed record JobRunRecord(
    Guid RunId,
    string JobName,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    JobRunOutcome Outcome,
    string? ErrorMessage,
    int FilesCopied,
    int FilesDeleted,
    int ErrorCount,
    long BytesCopied,
    TimeSpan? PullDuration,
    TimeSpan? PushDuration);
