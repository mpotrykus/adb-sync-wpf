using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Persistence;
using Microsoft.Data.Sqlite;

namespace AdbSync.Core.Tests.Persistence;

public class EngineDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public EngineDatabaseTests()
    {
        _paths = new AppPaths(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static EngineMigration Table(int version, string table, Func<SqliteConnection, SqliteTransaction, AppPaths, CancellationToken, Task<IReadOnlyList<string>>>? import = null) =>
        new(version, $"CREATE TABLE {table} (Value TEXT);", import);

    [Fact]
    public async Task OpenAsync_FreshDatabase_AppliesEveryMigrationAndUsesWal()
    {
        var db = new EngineDatabase(_paths, [Table(1, "A"), Table(2, "B")]);

        await using var connection = await db.OpenAsync();

        Assert.Equal(2, await ScalarAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(2, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('A', 'B');"));
        await using var mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string?)await mode.ExecuteScalarAsync());
    }

    [Fact]
    public async Task OpenAsync_ExistingDatabase_OnlyAppliesNewerMigrations()
    {
        await (await new EngineDatabase(_paths, [Table(1, "A")]).OpenAsync()).DisposeAsync();

        await using var connection = await new EngineDatabase(_paths, [Table(1, "A"), Table(2, "B")]).OpenAsync();

        Assert.Equal(2, await ScalarAsync(connection, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task OpenAsync_ImportedFiles_AreMovedToTheBackupFolderAfterCommit()
    {
        var legacy = Path.Combine(_paths.ConfigDir, "settings.json");
        Directory.CreateDirectory(_paths.ConfigDir);
        File.WriteAllText(legacy, "{}");
        var db = new EngineDatabase(_paths, [Table(1, "A", async (connection, transaction, paths, ct) =>
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO A (Value) VALUES ('imported');";
            await insert.ExecuteNonQueryAsync(ct);
            return [legacy];
        })]);

        await using var connection = await db.OpenAsync();

        Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM A;"));
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(_root, EngineDatabase.BackupFolderName, "config", "settings.json")));
    }

    [Fact]
    public async Task OpenAsync_FailingImport_RollsBackAndLeavesTheJsonInPlace()
    {
        var legacy = Path.Combine(_paths.ConfigDir, "settings.json");
        Directory.CreateDirectory(_paths.ConfigDir);
        File.WriteAllText(legacy, "{}");
        var db = new EngineDatabase(_paths, [Table(1, "A", (_, _, _, _) => throw new InvalidOperationException("bad json"))]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.OpenAsync());

        Assert.True(File.Exists(legacy));
        await using var connection = await new EngineDatabase(_paths, []).OpenAsync();
        Assert.Equal(0, await ScalarAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'A';"));
    }

    [Fact]
    public void InstanceLock_SecondAcquireFailsUntilTheFirstIsReleased()
    {
        var first = InstanceLock.TryAcquire(_paths, "first");
        Assert.NotNull(first);

        Assert.Null(InstanceLock.TryAcquire(_paths, "second"));

        first.Dispose();
        using var third = InstanceLock.TryAcquire(_paths, "third");
        Assert.NotNull(third);
    }
}
