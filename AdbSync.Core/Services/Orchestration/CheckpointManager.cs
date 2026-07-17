using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Common;
using AdbSync.Core.Services.Config;

namespace AdbSync.Core.Services.Orchestration;

public sealed class CheckpointManager(AppPaths paths) : ICheckpointManager
{
    public Task SaveAsync(string jobName, SyncCheckpoint checkpoint, CancellationToken ct = default) =>
        JsonFileIo.WriteAtomicAsync(GetPath(jobName), checkpoint, AppConfigJsonContext.Default.SyncCheckpoint, ct);

    public Task<SyncCheckpoint?> LoadAsync(string jobName, CancellationToken ct = default) =>
        JsonFileIo.ReadAsync(GetPath(jobName), AppConfigJsonContext.Default.SyncCheckpoint, ct);

    public Task ClearAsync(string jobName, CancellationToken ct = default)
    {
        var path = GetPath(jobName);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<SyncCheckpoint>> LoadAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(paths.CheckpointsDir))
            return [];

        var results = new List<SyncCheckpoint>();
        foreach (var file in Directory.EnumerateFiles(paths.CheckpointsDir, "*.json"))
        {
            var checkpoint = await JsonFileIo.ReadAsync(file, AppConfigJsonContext.Default.SyncCheckpoint, ct);
            if (checkpoint is not null)
                results.Add(checkpoint);
        }
        return results;
    }

    private string GetPath(string jobName) => Path.Combine(paths.CheckpointsDir, $"{jobName}.json");
}
