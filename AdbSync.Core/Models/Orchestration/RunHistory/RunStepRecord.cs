namespace AdbSync.Core.Models.Orchestration.RunHistory;

public enum RunStepStatus { Pending, Waiting, Running, Succeeded, Failed, Skipped, Cancelled }

public sealed record RunStepRecord(
    string DeviceName,
    SyncPhase Phase,
    RunStepStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int FilesCopied = 0,
    int FilesDeleted = 0,
    long BytesCopied = 0,
    int Errors = 0,
    int Conflicts = 0);
