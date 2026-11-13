namespace AdbSync.Engine.Services;

/// <summary>What started a run. Maps onto the API's RunTrigger.</summary>
public enum RunTrigger { Manual, Schedule, OnChange, ConflictResolved, CloseAndSync, RunAll, MissedSchedule }

/// <summary>A run as it's queued: its id is fixed here, before it starts, so clients can follow it immediately.</summary>
public sealed record RunRequest(
    Guid RunId, Guid JobId, string JobName, RunTrigger Trigger, bool DryRun, bool ForcePush, DateTimeOffset QueuedAt, Guid? RequestedBy);
