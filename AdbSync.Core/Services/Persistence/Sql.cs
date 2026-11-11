using Microsoft.Data.Sqlite;

namespace AdbSync.Core.Services.Persistence;

public static class Sql
{
    public static async Task<int> ExecuteAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = Create(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<List<T>> QueryAsync<T>(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, Func<SqliteDataReader, T> map, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Create(connection, transaction, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<T>();
        while (await reader.ReadAsync(ct))
            results.Add(map(reader));
        return results;
    }

    private static SqliteCommand Create(SqliteConnection connection, SqliteTransaction? transaction, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
