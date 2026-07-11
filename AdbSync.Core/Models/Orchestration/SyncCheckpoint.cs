namespace AdbSync.Core.Models.Orchestration;

/// <summary>Resumable mid-run state, written after every device-phase transition so a crashed run can pick up where it left off.</summary>
public sealed record SyncCheckpoint(
    int Version,
    DateTimeOffset SavedAt,
    int ProjectIndex,
    string? ProjectName,
    SyncPhase Phase,
    int DeviceIndex,
    Dictionary<string, string> DeviceSerials);
