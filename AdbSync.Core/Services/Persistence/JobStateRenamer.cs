using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Orchestration.RunHistory;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AdbSync.Core.Services.Persistence;

/// <summary>
/// Moves everything stored under a job's name to its new name: manifests, the manifest root, the resume checkpoint,
/// push-safety history, pending conflicts (and their saved device copies) and run history. The project folder itself is
/// moved by the caller.
/// </summary>
public sealed class JobStateRenamer(EngineDatabase database, AppPaths paths, IRunHistoryStore runHistory)
{
    public async Task RenameAsync(string oldName, string newName, CancellationToken ct = default)
    {
        ConfigNames.Segment(newName);

        await using (var connection = await database.OpenAsync(ct))
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct))
        {
            foreach (var table in new[] { "Manifests", "ManifestRoots", "PushSafety" })
                await Sql.ExecuteAsync(connection, transaction, $"UPDATE {table} SET JobName = @new WHERE JobName = @old;", ct, ("@new", newName), ("@old", oldName));

            var checkpoints = await Sql.QueryAsync(connection, transaction, "SELECT Json FROM Checkpoints WHERE JobName = @old;", r => r.GetString(0), ct, ("@old", oldName));
            foreach (var json in checkpoints)
            {
                var checkpoint = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.SyncCheckpoint)! with { ProjectName = newName };
                await Sql.ExecuteAsync(connection, transaction, "UPDATE Checkpoints SET JobName = @new, Json = @json WHERE JobName = @old;", ct,
                    ("@new", newName), ("@old", oldName), ("@json", JsonSerializer.Serialize(checkpoint, AppConfigJsonContext.Default.SyncCheckpoint)));
            }

            var conflicts = await Sql.QueryAsync(connection, transaction, "SELECT Id, Json FROM PendingConflicts WHERE JobName = @old;",
                r => (Id: r.GetString(0), Json: r.GetString(1)), ct, ("@old", oldName));
            foreach (var (id, json) in conflicts)
            {
                var conflict = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.PendingConflict)!;
                conflict.JobName = newName;
                await Sql.ExecuteAsync(connection, transaction, "UPDATE PendingConflicts SET JobName = @new, Json = @json WHERE Id = @id;", ct,
                    ("@new", newName), ("@id", id), ("@json", JsonSerializer.Serialize(conflict, AppConfigJsonContext.Default.PendingConflict)));
            }

            await transaction.CommitAsync(ct);
        }

        var oldCopies = Path.Combine(paths.PendingConflictsDir, ConfigNames.Segment(oldName));
        var newCopies = Path.Combine(paths.PendingConflictsDir, newName);
        if (Directory.Exists(oldCopies) && !Directory.Exists(newCopies))
            Directory.Move(oldCopies, newCopies);

        await runHistory.RenameJobAsync(oldName, newName, ct);
    }
}
