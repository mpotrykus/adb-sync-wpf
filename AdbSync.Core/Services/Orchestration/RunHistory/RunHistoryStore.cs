using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using System.Globalization;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using Microsoft.Data.Sqlite;

namespace AdbSync.Core.Services.Orchestration.RunHistory;

public sealed class RunHistoryStore : IRunHistoryStore
{
    private const int MaxRunsPerJob = 50;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public RunHistoryStore(AppPaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        // Pooling off: each call already opens/closes its own connection, and pooled handles would otherwise
        // keep the file locked past Dispose - fatal for tests that delete their temp AppPaths root right after.
        _connectionString = $"Data Source={paths.RunHistoryDbFile};Pooling=False";
    }

    public async Task SaveRunAsync(JobRunRecord record, string logText, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = connection.BeginTransaction();

        await using (var insertRun = connection.CreateCommand())
        {
            insertRun.Transaction = transaction;
            insertRun.CommandText = """
                INSERT INTO Runs (RunId, JobName, StartedAt, CompletedAt, Outcome, ErrorMessage, FilesCopied, FilesDeleted, ErrorCount, BytesCopied, PullDurationTicks, PushDurationTicks)
                VALUES (@runId, @jobName, @startedAt, @completedAt, @outcome, @errorMessage, @filesCopied, @filesDeleted, @errorCount, @bytesCopied, @pullDurationTicks, @pushDurationTicks);
                """;
            insertRun.Parameters.AddWithValue("@runId", record.RunId.ToString("N"));
            insertRun.Parameters.AddWithValue("@jobName", record.JobName);
            insertRun.Parameters.AddWithValue("@startedAt", record.StartedAt.ToString("O"));
            insertRun.Parameters.AddWithValue("@completedAt", record.CompletedAt.ToString("O"));
            insertRun.Parameters.AddWithValue("@outcome", record.Outcome.ToString());
            insertRun.Parameters.AddWithValue("@errorMessage", (object?)record.ErrorMessage ?? DBNull.Value);
            insertRun.Parameters.AddWithValue("@filesCopied", record.FilesCopied);
            insertRun.Parameters.AddWithValue("@filesDeleted", record.FilesDeleted);
            insertRun.Parameters.AddWithValue("@errorCount", record.ErrorCount);
            insertRun.Parameters.AddWithValue("@bytesCopied", record.BytesCopied);
            insertRun.Parameters.AddWithValue("@pullDurationTicks", (object?)record.PullDuration?.Ticks ?? DBNull.Value);
            insertRun.Parameters.AddWithValue("@pushDurationTicks", (object?)record.PushDuration?.Ticks ?? DBNull.Value);
            await insertRun.ExecuteNonQueryAsync(ct);
        }

        await using (var insertLog = connection.CreateCommand())
        {
            insertLog.Transaction = transaction;
            insertLog.CommandText = "INSERT INTO RunLogs (RunId, LogText) VALUES (@runId, @logText);";
            insertLog.Parameters.AddWithValue("@runId", record.RunId.ToString("N"));
            insertLog.Parameters.AddWithValue("@logText", logText);
            await insertLog.ExecuteNonQueryAsync(ct);
        }

        await using (var trim = connection.CreateCommand())
        {
            trim.Transaction = transaction;
            trim.CommandText = """
                DELETE FROM Runs WHERE JobName = @jobName AND RunId NOT IN (
                    SELECT RunId FROM Runs WHERE JobName = @jobName ORDER BY StartedAt DESC LIMIT @maxRuns);
                """;
            trim.Parameters.AddWithValue("@jobName", record.JobName);
            trim.Parameters.AddWithValue("@maxRuns", MaxRunsPerJob);
            await trim.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<JobRunRecord>> ListRunsAsync(string jobName, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RunId, JobName, StartedAt, CompletedAt, Outcome, ErrorMessage, FilesCopied, FilesDeleted, ErrorCount, BytesCopied, PullDurationTicks, PushDurationTicks
            FROM Runs WHERE JobName = @jobName ORDER BY StartedAt DESC;
            """;
        command.Parameters.AddWithValue("@jobName", jobName);

        var results = new List<JobRunRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new JobRunRecord(
                Guid.ParseExact(reader.GetString(0), "N"),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                Enum.Parse<JobRunOutcome>(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt64(9),
                reader.IsDBNull(10) ? null : TimeSpan.FromTicks(reader.GetInt64(10)),
                reader.IsDBNull(11) ? null : TimeSpan.FromTicks(reader.GetInt64(11))));
        }
        return results;
    }

    public async Task<string?> GetRunLogAsync(string jobName, Guid runId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT LogText FROM RunLogs WHERE RunId = @runId;";
        command.Parameters.AddWithValue("@runId", runId.ToString("N"));

        return await command.ExecuteScalarAsync(ct) as string;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // SQLite enforces this per-connection (not persisted in the database file), so it must run on every open.
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(ct);
        }

        await EnsureSchemaAsync(connection, ct);
        return connection;
    }

    // Columns added after the initial release - applied via ALTER TABLE so existing run-history.db files
    // (which only have the original columns) pick them up instead of losing their history.
    private static readonly (string Name, string Definition)[] AddedRunColumns =
    [
        ("FilesCopied", "INTEGER NOT NULL DEFAULT 0"),
        ("FilesDeleted", "INTEGER NOT NULL DEFAULT 0"),
        ("ErrorCount", "INTEGER NOT NULL DEFAULT 0"),
        ("BytesCopied", "INTEGER NOT NULL DEFAULT 0"),
        ("PullDurationTicks", "INTEGER NULL"),
        ("PushDurationTicks", "INTEGER NULL"),
    ];

    private async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (_schemaReady)
            return;

        await _schemaGate.WaitAsync(ct);
        try
        {
            if (_schemaReady)
                return;

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS Runs (
                        RunId TEXT PRIMARY KEY,
                        JobName TEXT NOT NULL,
                        StartedAt TEXT NOT NULL,
                        CompletedAt TEXT NOT NULL,
                        Outcome TEXT NOT NULL,
                        ErrorMessage TEXT NULL);
                    CREATE INDEX IF NOT EXISTS IX_Runs_JobName_StartedAt ON Runs(JobName, StartedAt DESC);
                    CREATE TABLE IF NOT EXISTS RunLogs (
                        RunId TEXT PRIMARY KEY REFERENCES Runs(RunId) ON DELETE CASCADE,
                        LogText TEXT NOT NULL);
                    """;
                await command.ExecuteNonQueryAsync(ct);
            }

            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(Runs);";
                await using var reader = await pragma.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    existingColumns.Add(reader.GetString(reader.GetOrdinal("name")));
            }

            foreach (var (name, definition) in AddedRunColumns)
            {
                if (existingColumns.Contains(name))
                    continue;

                await using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE Runs ADD COLUMN {name} {definition};";
                await alter.ExecuteNonQueryAsync(ct);
            }

            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }
}
