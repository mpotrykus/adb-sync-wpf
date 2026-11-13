using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Common;
using AdbSync.Core.Services.Persistence;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdbSync.Core.Services.Config;

/// <summary>
/// Loads and saves the composed <see cref="AppConfig"/> in the engine database. Jobs and devices are one JSON document
/// per row with a stable id, a case-insensitively unique name and a version that changes whenever the document does.
/// Runtime state the engine writes (last run times, cached device endpoints) lives in separate tables, not in the
/// documents. <see cref="SaveAsync"/> takes the whole config, as the app edits it in place, and writes only what changed,
/// in one transaction.
/// </summary>
public sealed class AppConfigStore(EngineDatabase database, AppPaths paths) : IAppConfigStore
{
    public AppConfigStore(AppPaths paths) : this(new EngineDatabase(paths), paths) { }

    public async Task<AppConfig> LoadAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        var config = await LoadAsync(connection, ct);
        if (string.IsNullOrWhiteSpace(config.Settings.ProjectsDirectory))
            config.Settings.ProjectsDirectory = paths.DefaultProjectsDirectory;
        return config;
    }

    public async Task SaveAsync(AppConfig config, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await SaveAsync(connection, transaction, config, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Migration 1's import: the three legacy JSON config files, if present.</summary>
    internal static async Task<IReadOnlyList<string>> ImportLegacyJsonAsync(
        SqliteConnection connection, SqliteTransaction transaction, AppPaths paths, CancellationToken ct)
    {
        var settings = await JsonFileIo.ReadAsync(paths.SettingsFile, AppConfigJsonContext.Default.GlobalSettings, ct);
        var devices = await JsonFileIo.ReadAsync(paths.DevicesFile, AppConfigJsonContext.Default.DevicesFile, ct);
        var projects = await JsonFileIo.ReadAsync(paths.ProjectsFile, AppConfigJsonContext.Default.ProjectsFile, ct);
        if (settings is null && devices is null && projects is null)
            return [];

        var config = new AppConfig
        {
            Settings = settings ?? new GlobalSettings(),
            Devices = devices?.Devices ?? [],
            Jobs = projects?.Projects ?? [],
        };
        await SaveAsync(connection, transaction, config, ct);
        return [paths.SettingsFile, paths.DevicesFile, paths.ProjectsFile];
    }

    private static async Task<AppConfig> LoadAsync(SqliteConnection connection, CancellationToken ct)
    {
        var config = new AppConfig();

        await using (var settings = connection.CreateCommand())
        {
            settings.CommandText = "SELECT Json FROM Settings WHERE Id = 1;";
            if (await settings.ExecuteScalarAsync(ct) is string json)
                config.Settings = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.GlobalSettings) ?? new GlobalSettings();
        }

        await using (var devices = connection.CreateCommand())
        {
            devices.CommandText = """
                SELECT d.Id, d.Json, r.CachedHostPort, r.CachedAt
                FROM Devices d LEFT JOIN DeviceRuntime r ON r.DeviceId = d.Id
                ORDER BY d.Position;
                """;
            await using var reader = await devices.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var device = JsonSerializer.Deserialize(reader.GetString(1), AppConfigJsonContext.Default.DeviceConfig)!;
                device.Id = Guid.Parse(reader.GetString(0));
                device.CachedHostPort = reader.IsDBNull(2) ? null : reader.GetString(2);
                device.CachedAt = ReadTime(reader, 3);
                config.Devices.Add(device);
            }
        }

        var deviceNames = config.Devices.ToDictionary(d => d.Id, d => d.Name);
        await using (var jobs = connection.CreateCommand())
        {
            jobs.CommandText = """
                SELECT j.Id, j.Json, r.LastRunAt, r.LastSuccessAt
                FROM Jobs j LEFT JOIN JobRuntime r ON r.JobId = j.Id
                ORDER BY j.Position;
                """;
            await using var reader = await jobs.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var job = JsonSerializer.Deserialize(reader.GetString(1), AppConfigJsonContext.Default.SyncJobConfig)!;
                job.Id = Guid.Parse(reader.GetString(0));
                job.Schedule.LastRunAt = ReadTime(reader, 2);
                job.Schedule.LastSuccessAt = ReadTime(reader, 3);
                foreach (var binding in job.Devices)
                {
                    if (deviceNames.TryGetValue(binding.DeviceId, out var name))
                        binding.DeviceName = name;
                }
                config.Jobs.Add(job);
            }
        }

        return config;
    }

    private static async Task SaveAsync(SqliteConnection connection, SqliteTransaction transaction, AppConfig config, CancellationToken ct)
    {
        foreach (var device in config.Devices.Where(d => d.Id == Guid.Empty))
            device.Id = Guid.NewGuid();
        foreach (var job in config.Jobs.Where(j => j.Id == Guid.Empty))
            job.Id = Guid.NewGuid();

        var deviceIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in config.Devices)
            deviceIds.TryAdd(device.Name, device.Id);
        foreach (var binding in config.Jobs.SelectMany(j => j.Devices))
        {
            if (deviceIds.TryGetValue(binding.DeviceName, out var id))
                binding.DeviceId = id;
        }

        await UpsertSettingsAsync(connection, transaction, ToJson(config.Settings), ct);

        await DeleteMissingAsync(connection, transaction, "Jobs", config.Jobs.Select(j => j.Id), ct);
        await DeleteMissingAsync(connection, transaction, "Devices", config.Devices.Select(d => d.Id), ct);

        for (var i = 0; i < config.Devices.Count; i++)
        {
            var device = config.Devices[i];
            await UpsertDocumentAsync(connection, transaction, "Devices", device.Id, device.Name, i, ToJson(device), ct);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO DeviceRuntime (DeviceId, CachedHostPort, CachedAt) VALUES (@id, @hostPort, @cachedAt)
                ON CONFLICT (DeviceId) DO UPDATE SET CachedHostPort = excluded.CachedHostPort, CachedAt = excluded.CachedAt;
                """, ct, ("@id", device.Id.ToString()), ("@hostPort", device.CachedHostPort), ("@cachedAt", WriteTime(device.CachedAt)));
        }

        for (var i = 0; i < config.Jobs.Count; i++)
        {
            var job = config.Jobs[i];
            await UpsertDocumentAsync(connection, transaction, "Jobs", job.Id, job.Name, i, ToJson(job), ct);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO JobRuntime (JobId, LastRunAt, LastSuccessAt) VALUES (@id, @lastRunAt, @lastSuccessAt)
                ON CONFLICT (JobId) DO UPDATE SET LastRunAt = excluded.LastRunAt, LastSuccessAt = excluded.LastSuccessAt;
                """, ct, ("@id", job.Id.ToString()), ("@lastRunAt", WriteTime(job.Schedule.LastRunAt)), ("@lastSuccessAt", WriteTime(job.Schedule.LastSuccessAt)));
        }
    }

    private static string ToJson(GlobalSettings settings) =>
        JsonSerializer.Serialize(settings, AppConfigJsonContext.Default.GlobalSettings);

    private static string ToJson(DeviceConfig device)
    {
        var node = JsonSerializer.SerializeToNode(device, AppConfigJsonContext.Default.DeviceConfig)!.AsObject();
        node.Remove("id");
        node.Remove("cachedHostPort");
        node.Remove("cachedAt");
        return node.ToJsonString();
    }

    private static string ToJson(SyncJobConfig job)
    {
        var node = JsonSerializer.SerializeToNode(job, AppConfigJsonContext.Default.SyncJobConfig)!.AsObject();
        node.Remove("id");
        if (node["schedule"] is JsonObject schedule)
        {
            schedule.Remove("lastRunAt");
            schedule.Remove("lastSuccessAt");
        }
        return node.ToJsonString();
    }

    private static async Task UpsertSettingsAsync(SqliteConnection connection, SqliteTransaction transaction, string json, CancellationToken ct) =>
        await ExecuteAsync(connection, transaction, """
            INSERT INTO Settings (Id, Json, Version) VALUES (1, @json, 1)
            ON CONFLICT (Id) DO UPDATE SET Json = excluded.Json, Version = Version + 1 WHERE Json <> excluded.Json;
            """, ct, ("@json", json));

    private static async Task UpsertDocumentAsync(
        SqliteConnection connection, SqliteTransaction transaction, string table, Guid id, string name, int position, string json, CancellationToken ct) =>
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO {table} (Id, Name, Position, Json, Version) VALUES (@id, @name, @position, @json, 1)
            ON CONFLICT (Id) DO UPDATE SET
                Name = excluded.Name,
                Position = excluded.Position,
                Json = excluded.Json,
                Version = CASE WHEN Json <> excluded.Json THEN Version + 1 ELSE Version END;
            """, ct, ("@id", id.ToString()), ("@name", name), ("@position", position), ("@json", json));

    private static async Task DeleteMissingAsync(
        SqliteConnection connection, SqliteTransaction transaction, string table, IEnumerable<Guid> keep, CancellationToken ct)
    {
        var keepIds = keep.Select(id => id.ToString()).ToHashSet();
        var existing = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT Id FROM {table};";
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                existing.Add(reader.GetString(0));
        }
        foreach (var id in existing.Where(id => !keepIds.Contains(id)))
            await ExecuteAsync(connection, transaction, $"DELETE FROM {table} WHERE Id = @id;", ct, ("@id", id));
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string? WriteTime(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
