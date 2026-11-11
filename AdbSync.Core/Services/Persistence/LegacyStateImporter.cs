using AdbSync.Core.Models.Merge;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Merge;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AdbSync.Core.Services.Persistence;

/// <summary>
/// Migration 2's import: manifests, checkpoints, push-safety history and pending conflicts from their JSON files. A file
/// that can't be read (for example one left zero-length by a power cut) is skipped and stays where it is, so one bad file
/// doesn't stop the app from starting; the job rebuilds that state on its next run.
/// </summary>
internal static class LegacyStateImporter
{
    private const string ManifestSuffix = ".manifest.json";
    private const string PendingSuffix = ".pending.json";

    public static async Task<IReadOnlyList<string>> ImportAsync(SqliteConnection connection, SqliteTransaction transaction, AppPaths paths, CancellationToken ct)
    {
        var imported = new List<string>();

        foreach (var (file, jobName) in FilesIn(paths.ManifestsDir, ManifestSuffix, perJobFolder: true))
        {
            var deviceName = Path.GetFileName(file)[..^ManifestSuffix.Length];
            if (Read(file, AppConfigJsonContext.Default.SyncManifest) is not { } manifest)
                continue;
            await Sql.ExecuteAsync(connection, transaction,
                "INSERT OR REPLACE INTO Manifests (JobName, DeviceName, Json) VALUES (@job, @device, @json);", ct,
                ("@job", jobName), ("@device", deviceName), ("@json", JsonSerializer.Serialize(manifest, AppConfigJsonContext.Default.SyncManifest)));
            imported.Add(file);
        }

        foreach (var (file, jobName) in FilesIn(paths.CheckpointsDir, ".json", perJobFolder: false))
        {
            if (Read(file, AppConfigJsonContext.Default.SyncCheckpoint) is not { } checkpoint)
                continue;
            await Sql.ExecuteAsync(connection, transaction,
                "INSERT OR REPLACE INTO Checkpoints (JobName, Json) VALUES (@job, @json);", ct,
                ("@job", jobName), ("@json", JsonSerializer.Serialize(checkpoint, AppConfigJsonContext.Default.SyncCheckpoint)));
            imported.Add(file);
        }

        foreach (var (file, jobName) in FilesIn(Path.Combine(paths.Root, "push-safety"), ".json", perJobFolder: false))
        {
            if (Read(file, AppConfigJsonContext.Default.PushSafetyHistory) is not { } history)
                continue;
            await Sql.ExecuteAsync(connection, transaction,
                "INSERT OR REPLACE INTO PushSafety (JobName, Json) VALUES (@job, @json);", ct,
                ("@job", jobName), ("@json", JsonSerializer.Serialize(history, AppConfigJsonContext.Default.PushSafetyHistory)));
            imported.Add(file);
        }

        foreach (var (file, _) in FilesIn(paths.PendingConflictsDir, PendingSuffix, perJobFolder: true))
        {
            if (Read(file, AppConfigJsonContext.Default.PendingConflictsFile) is not { } pending)
                continue;
            foreach (var conflict in pending.Items)
                await PendingConflictStore.InsertAsync(connection, transaction, conflict, ct);
            imported.Add(file);
        }

        return imported;
    }

    private static IEnumerable<(string File, string JobName)> FilesIn(string dir, string suffix, bool perJobFolder)
    {
        if (!Directory.Exists(dir))
            yield break;

        if (perJobFolder)
        {
            foreach (var jobDir in Directory.GetDirectories(dir))
            foreach (var file in Directory.GetFiles(jobDir, $"*{suffix}"))
                yield return (file, Path.GetFileName(jobDir));
        }
        else
        {
            foreach (var file in Directory.GetFiles(dir, $"*{suffix}"))
                yield return (file, Path.GetFileName(file)[..^suffix.Length]);
        }
    }

    private static T? Read<T>(string file, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file), typeInfo);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}
