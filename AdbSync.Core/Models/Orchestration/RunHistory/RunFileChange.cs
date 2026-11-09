namespace AdbSync.Core.Models.Orchestration.RunHistory;

public enum RunFileAction { Copied, Created, Updated, Deleted, Conflict, Held, Error }

/// <summary>One file a run touched, tied to the device and phase that did it. <see cref="Detail"/> is free text such as
/// "device → master" or an error message; <see cref="Path"/> is empty for an error that isn't tied to one file.</summary>
public sealed record RunFileChange(
    string DeviceName,
    SyncPhase Phase,
    string Path,
    RunFileAction Action,
    long? Size = null,
    string? Detail = null);
