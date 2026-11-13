using AdbSync.Core.Services.Config;
using Microsoft.Data.Sqlite;

namespace AdbSync.Core.Services.Persistence;

/// <summary>
/// One schema step. <see cref="Sql"/> runs first, then <see cref="Import"/> (if any) copies the legacy JSON files this
/// version replaces into the new tables, in the same transaction. The paths it returns are moved into the backup folder
/// once the transaction commits, so a failed import leaves the JSON files where they were.
/// </summary>
public sealed record EngineMigration(
    int Version,
    string Sql,
    Func<SqliteConnection, SqliteTransaction, AppPaths, CancellationToken, Task<IReadOnlyList<string>>>? Import = null);

/// <summary>The engine's SQLite database (<c>adbsync.db</c> in the data root): config, manifests and other sync state.</summary>
public sealed class EngineDatabase
{
    public const string BackupFolderName = "json-backup";

    private readonly AppPaths _paths;
    private readonly IReadOnlyList<EngineMigration> _migrations;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialized;

    public EngineDatabase(AppPaths paths) : this(paths, EngineMigrations.All) { }

    public EngineDatabase(AppPaths paths, IReadOnlyList<EngineMigration> migrations)
    {
        _paths = paths;
        _migrations = migrations.OrderBy(m => m.Version).ToList();
        // Pooling keeps the file open after a connection is disposed, which blocks deleting the data folder on Windows.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = paths.EngineDbFile, Pooling = false }.ToString();
    }

    public int LatestVersion => _migrations.Count == 0 ? 0 : _migrations[^1].Version;

    /// <summary>An open connection with the schema up to date and the per-connection pragmas applied.</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        if (!_initialized)
            await InitializeAsync(ct);
        return await OpenRawAsync(ct);
    }

    private async Task<SqliteConnection> OpenRawAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var pragmas = connection.CreateCommand();
        pragmas.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
        await pragmas.ExecuteNonQueryAsync(ct);
        return connection;
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        await _initGate.WaitAsync(ct);
        try
        {
            if (_initialized)
                return;

            Directory.CreateDirectory(_paths.Root);
            await using var connection = await OpenRawAsync(ct);
            await using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode = WAL;";
                await wal.ExecuteNonQueryAsync(ct);
            }

            var current = await GetUserVersionAsync(connection, ct);
            var imported = new List<string>();
            foreach (var migration in _migrations.Where(m => m.Version > current))
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = $"{migration.Sql}\nPRAGMA user_version = {migration.Version};";
                    await command.ExecuteNonQueryAsync(ct);
                }
                var files = migration.Import is null ? [] : await migration.Import(connection, transaction, _paths, ct);
                await transaction.CommitAsync(ct);
                imported.AddRange(files);
            }

            BackUp(imported);
            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private static async Task<int> GetUserVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    private void BackUp(IEnumerable<string> files)
    {
        var backupRoot = Path.Combine(_paths.Root, BackupFolderName);
        foreach (var file in files.Distinct(StringComparer.Ordinal))
        {
            if (!File.Exists(file))
                continue;
            var destination = Path.Combine(backupRoot, Path.GetRelativePath(_paths.Root, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination))
                destination = $"{destination}.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Move(file, destination);
        }
    }
}
