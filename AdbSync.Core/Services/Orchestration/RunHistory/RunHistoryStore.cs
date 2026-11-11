using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using AdbSync.Core.Services.Config;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace AdbSync.Core.Services.Orchestration.RunHistory;

public sealed class RunHistoryStore : IRunHistoryStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public RunHistoryStore(AppPaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        _connectionString = $"Data Source={paths.RunHistoryDbFile};Pooling=False";
    }

    public Task SaveRunAsync(JobRunRecord record, string logText, int maxRuns, CancellationToken ct = default) =>
        SaveRunAsync(record, logText, maxRuns, null, ct);

    public Task SaveRunAsync(JobRunRecord record, string logText, int maxRuns, IReadOnlyList<RunStepRecord>? steps, CancellationToken ct = default) =>
        SaveRunAsync(record, logText, maxRuns, steps, null, 0, ct);

    public async Task SaveRunAsync(
        JobRunRecord record, string logText, int maxRuns, IReadOnlyList<RunStepRecord>? steps,
        IReadOnlyList<RunFileChange>? files, int keepFileDetailRuns, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = connection.BeginTransaction();

        await using (var insertRun = connection.CreateCommand())
        {
            insertRun.Transaction = transaction;
            insertRun.CommandText = """
                INSERT INTO Runs (RunId, JobName, StartedAt, CompletedAt, Outcome, ErrorMessage, FilesCopied, FilesDeleted, ErrorCount, BytesCopied, PullDurationTicks, PushDurationTicks, ErrorCode)
                VALUES (@runId, @jobName, @startedAt, @completedAt, @outcome, @errorMessage, @filesCopied, @filesDeleted, @errorCount, @bytesCopied, @pullDurationTicks, @pushDurationTicks, @errorCode);
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
            insertRun.Parameters.AddWithValue("@errorCode", (object?)record.ErrorCode?.ToString() ?? DBNull.Value);
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

        for (var i = 0; i < (steps?.Count ?? 0); i++)
        {
            var step = steps![i];
            await using var insertStep = connection.CreateCommand();
            insertStep.Transaction = transaction;
            insertStep.CommandText = """
                INSERT INTO RunSteps (RunId, Seq, DeviceName, Phase, Status, StartedAt, EndedAt, FilesCopied, FilesDeleted, BytesCopied, Errors, Conflicts)
                VALUES (@runId, @seq, @deviceName, @phase, @status, @startedAt, @endedAt, @filesCopied, @filesDeleted, @bytesCopied, @errors, @conflicts);
                """;
            insertStep.Parameters.AddWithValue("@runId", record.RunId.ToString("N"));
            insertStep.Parameters.AddWithValue("@seq", i);
            insertStep.Parameters.AddWithValue("@deviceName", step.DeviceName);
            insertStep.Parameters.AddWithValue("@phase", step.Phase.ToString());
            insertStep.Parameters.AddWithValue("@status", step.Status.ToString());
            insertStep.Parameters.AddWithValue("@startedAt", (object?)step.StartedAt?.ToString("O") ?? DBNull.Value);
            insertStep.Parameters.AddWithValue("@endedAt", (object?)step.EndedAt?.ToString("O") ?? DBNull.Value);
            insertStep.Parameters.AddWithValue("@filesCopied", step.FilesCopied);
            insertStep.Parameters.AddWithValue("@filesDeleted", step.FilesDeleted);
            insertStep.Parameters.AddWithValue("@bytesCopied", step.BytesCopied);
            insertStep.Parameters.AddWithValue("@errors", step.Errors);
            insertStep.Parameters.AddWithValue("@conflicts", step.Conflicts);
            await insertStep.ExecuteNonQueryAsync(ct);
        }

        if (files is { Count: > 0 })
        {
            await using var insertFile = connection.CreateCommand();
            insertFile.Transaction = transaction;
            insertFile.CommandText = """
                INSERT INTO RunFiles (RunId, Seq, DeviceName, Phase, Path, Action, Size, Detail)
                VALUES (@runId, @seq, @deviceName, @phase, @path, @action, @size, @detail);
                """;
            insertFile.Parameters.AddWithValue("@runId", record.RunId.ToString("N"));
            var seqParam = insertFile.Parameters.Add("@seq", SqliteType.Integer);
            var deviceParam = insertFile.Parameters.Add("@deviceName", SqliteType.Text);
            var phaseParam = insertFile.Parameters.Add("@phase", SqliteType.Text);
            var pathParam = insertFile.Parameters.Add("@path", SqliteType.Text);
            var actionParam = insertFile.Parameters.Add("@action", SqliteType.Text);
            var sizeParam = insertFile.Parameters.Add("@size", SqliteType.Integer);
            var detailParam = insertFile.Parameters.Add("@detail", SqliteType.Text);

            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                seqParam.Value = i;
                deviceParam.Value = file.DeviceName;
                phaseParam.Value = file.Phase.ToString();
                pathParam.Value = file.Path;
                actionParam.Value = file.Action.ToString();
                sizeParam.Value = (object?)file.Size ?? DBNull.Value;
                detailParam.Value = (object?)file.Detail ?? DBNull.Value;
                await insertFile.ExecuteNonQueryAsync(ct);
            }
        }

        if (keepFileDetailRuns > 0)
        {
            await using var pruneFiles = connection.CreateCommand();
            pruneFiles.Transaction = transaction;
            pruneFiles.CommandText = """
                DELETE FROM RunFiles WHERE RunId IN (
                    SELECT RunId FROM Runs WHERE JobName = @jobName ORDER BY StartedAt DESC LIMIT -1 OFFSET @keep);
                """;
            pruneFiles.Parameters.AddWithValue("@jobName", record.JobName);
            pruneFiles.Parameters.AddWithValue("@keep", keepFileDetailRuns);
            await pruneFiles.ExecuteNonQueryAsync(ct);
        }

        await using (var trim = connection.CreateCommand())
        {
            trim.Transaction = transaction;
            trim.CommandText = """
                DELETE FROM Runs WHERE JobName = @jobName AND RunId NOT IN (
                    SELECT RunId FROM Runs WHERE JobName = @jobName ORDER BY StartedAt DESC LIMIT @maxRuns);
                """;
            trim.Parameters.AddWithValue("@jobName", record.JobName);
            trim.Parameters.AddWithValue("@maxRuns", maxRuns);
            await trim.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<JobRunRecord>> ListRunsAsync(string jobName, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RunId, JobName, StartedAt, CompletedAt, Outcome, ErrorMessage, FilesCopied, FilesDeleted, ErrorCount, BytesCopied, PullDurationTicks, PushDurationTicks, ErrorCode
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
                reader.IsDBNull(11) ? null : TimeSpan.FromTicks(reader.GetInt64(11)),
                reader.IsDBNull(12) ? null : Enum.TryParse<RunErrorCode>(reader.GetString(12), out var code) ? code : RunErrorCode.InternalError));
        }
        return results;
    }

    public async Task<IReadOnlyList<RunFileChange>> GetRunFilesAsync(Guid runId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DeviceName, Phase, Path, Action, Size, Detail
            FROM RunFiles WHERE RunId = @runId ORDER BY Seq;
            """;
        command.Parameters.AddWithValue("@runId", runId.ToString("N"));

        var results = new List<RunFileChange>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RunFileChange(
                reader.GetString(0),
                Enum.Parse<SyncPhase>(reader.GetString(1)),
                reader.GetString(2),
                Enum.Parse<RunFileAction>(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return results;
    }

    public async Task<IReadOnlyList<RunStepRecord>> GetRunStepsAsync(Guid runId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DeviceName, Phase, Status, StartedAt, EndedAt, FilesCopied, FilesDeleted, BytesCopied, Errors, Conflicts
            FROM RunSteps WHERE RunId = @runId ORDER BY Seq;
            """;
        command.Parameters.AddWithValue("@runId", runId.ToString("N"));

        var results = new List<RunStepRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RunStepRecord(
                reader.GetString(0),
                Enum.Parse<SyncPhase>(reader.GetString(1)),
                Enum.Parse<RunStepStatus>(reader.GetString(2)),
                reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt64(7),
                reader.GetInt32(8),
                reader.GetInt32(9)));
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

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(ct);
        }

        await EnsureSchemaAsync(connection, ct);
        return connection;
    }

    private static readonly (string Name, string Definition)[] AddedRunColumns =
    [
        ("FilesCopied", "INTEGER NOT NULL DEFAULT 0"),
        ("FilesDeleted", "INTEGER NOT NULL DEFAULT 0"),
        ("ErrorCount", "INTEGER NOT NULL DEFAULT 0"),
        ("BytesCopied", "INTEGER NOT NULL DEFAULT 0"),
        ("PullDurationTicks", "INTEGER NULL"),
        ("PushDurationTicks", "INTEGER NULL"),
        ("ErrorCode", "TEXT NULL"),
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
                    CREATE TABLE IF NOT EXISTS RunSteps (
                        RunId TEXT NOT NULL REFERENCES Runs(RunId) ON DELETE CASCADE,
                        Seq INTEGER NOT NULL,
                        DeviceName TEXT NOT NULL,
                        Phase TEXT NOT NULL,
                        Status TEXT NOT NULL,
                        StartedAt TEXT NULL,
                        EndedAt TEXT NULL,
                        FilesCopied INTEGER NOT NULL DEFAULT 0,
                        FilesDeleted INTEGER NOT NULL DEFAULT 0,
                        BytesCopied INTEGER NOT NULL DEFAULT 0,
                        Errors INTEGER NOT NULL DEFAULT 0,
                        Conflicts INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (RunId, Seq));
                    CREATE TABLE IF NOT EXISTS RunFiles (
                        RunId TEXT NOT NULL REFERENCES Runs(RunId) ON DELETE CASCADE,
                        Seq INTEGER NOT NULL,
                        DeviceName TEXT NOT NULL,
                        Phase TEXT NOT NULL,
                        Path TEXT NOT NULL,
                        Action TEXT NOT NULL,
                        Size INTEGER NULL,
                        Detail TEXT NULL,
                        PRIMARY KEY (RunId, Seq));
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
