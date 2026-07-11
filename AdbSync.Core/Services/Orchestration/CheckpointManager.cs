using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;

namespace AdbSync.Core.Services.Orchestration;

public sealed class CheckpointManager(AppPaths paths) : ICheckpointManager
{
    public Task SaveAsync(SyncCheckpoint checkpoint, CancellationToken ct = default) =>
        JsonFileIo.WriteAtomicAsync(paths.CheckpointFile, checkpoint, AppConfigJsonContext.Default.SyncCheckpoint, ct);

    public Task<SyncCheckpoint?> LoadAsync(CancellationToken ct = default) =>
        JsonFileIo.ReadAsync(paths.CheckpointFile, AppConfigJsonContext.Default.SyncCheckpoint, ct);

    public Task ClearAsync(CancellationToken ct = default)
    {
        if (File.Exists(paths.CheckpointFile))
            File.Delete(paths.CheckpointFile);
        return Task.CompletedTask;
    }
}
