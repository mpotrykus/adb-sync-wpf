using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Persistence;
using System.Text.Json;

namespace AdbSync.Core.Services.Orchestration;

public sealed class CheckpointManager(EngineDatabase database) : ICheckpointManager
{
    public CheckpointManager(AppPaths paths) : this(new EngineDatabase(paths)) { }

    public async Task SaveAsync(string jobName, SyncCheckpoint checkpoint, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await Sql.ExecuteAsync(connection, null, """
            INSERT INTO Checkpoints (JobName, Json) VALUES (@job, @json)
            ON CONFLICT (JobName) DO UPDATE SET JobName = excluded.JobName, Json = excluded.Json;
            """, ct, ("@job", ConfigNames.Segment(jobName)), ("@json", JsonSerializer.Serialize(checkpoint, AppConfigJsonContext.Default.SyncCheckpoint)));
    }

    public async Task<SyncCheckpoint?> LoadAsync(string jobName, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        var rows = await Sql.QueryAsync(connection, null, "SELECT Json FROM Checkpoints WHERE JobName = @job;", r => r.GetString(0), ct, ("@job", jobName));
        return rows.Count == 0 ? null : JsonSerializer.Deserialize(rows[0], AppConfigJsonContext.Default.SyncCheckpoint);
    }

    public async Task ClearAsync(string jobName, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await Sql.ExecuteAsync(connection, null, "DELETE FROM Checkpoints WHERE JobName = @job;", ct, ("@job", jobName));
    }

    public async Task<IReadOnlyList<SyncCheckpoint>> LoadAllAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        var rows = await Sql.QueryAsync(connection, null, "SELECT Json FROM Checkpoints;", r => r.GetString(0), ct);
        return rows.Select(json => JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.SyncCheckpoint)!).ToList();
    }
}
