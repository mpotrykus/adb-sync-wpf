namespace AdbSync.Core.Orchestration;

public interface ICheckpointManager
{
    Task SaveAsync(SyncCheckpoint checkpoint, CancellationToken ct = default);
    Task<SyncCheckpoint?> LoadAsync(CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}
