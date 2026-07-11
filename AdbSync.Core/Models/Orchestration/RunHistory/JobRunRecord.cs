using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;

namespace AdbSync.Core.Models.Orchestration.RunHistory;

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
