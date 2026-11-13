namespace AdbSync.Core.Services.Persistence;

/// <summary>Whether the scheduler is paused, kept across restarts.</summary>
public sealed class SchedulerStateStore(EngineDatabase database)
{
    public async Task<bool> IsPausedAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Paused FROM SchedulerState WHERE Id = 1;";
        return await command.ExecuteScalarAsync(ct) is long paused && paused != 0;
    }

    public async Task SetPausedAsync(bool paused, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SchedulerState (Id, Paused) VALUES (1, @paused)
            ON CONFLICT (Id) DO UPDATE SET Paused = excluded.Paused;
            """;
        command.Parameters.AddWithValue("@paused", paused ? 1 : 0);
        await command.ExecuteNonQueryAsync(ct);
    }
}
