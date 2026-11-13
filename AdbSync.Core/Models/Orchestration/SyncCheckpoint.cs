namespace AdbSync.Core.Models.Orchestration;

/// <summary>Resumable mid-run state, written after every device finishes the current phase so a crashed run can
/// pick up where it left off. CompletedDevices names the devices already done in this phase - devices run
/// concurrently within a phase, so "done" is a set, not a single cutoff index. UnpulledPaths lists, per device,
/// files whose pull failed: their device copy is unknown, so the push must leave them alone even on a resume.</summary>
public sealed record SyncCheckpoint(
    int Version,
    DateTimeOffset SavedAt,
    int ProjectIndex,
    string? ProjectName,
    SyncPhase Phase,
    IReadOnlyList<string> CompletedDevices,
    Dictionary<string, string> DeviceSerials,
    Dictionary<string, List<string>>? UnpulledPaths = null);
