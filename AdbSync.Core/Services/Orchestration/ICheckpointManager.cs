using AdbSync.Core.Models.Orchestration;

namespace AdbSync.Core.Services.Orchestration;

public interface ICheckpointManager
{
    Task SaveAsync(string jobName, SyncCheckpoint checkpoint, CancellationToken ct = default);
    Task<SyncCheckpoint?> LoadAsync(string jobName, CancellationToken ct = default);
    Task ClearAsync(string jobName, CancellationToken ct = default);

    /// <summary>Enumerates every job's saved checkpoint, for hydrating the dashboard without knowing job names up front.</summary>
    Task<IReadOnlyList<SyncCheckpoint>> LoadAllAsync(CancellationToken ct = default);
}
