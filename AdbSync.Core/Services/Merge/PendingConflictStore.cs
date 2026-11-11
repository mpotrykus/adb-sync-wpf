using AdbSync.Core.Models.Merge;
using AdbSync.Core.Services.Common;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Persistence;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace AdbSync.Core.Services.Merge;

public sealed class PendingConflictStore(EngineDatabase database, AppPaths paths) : IPendingConflictStore
{
    private static readonly TimeSpan ModifiedTolerance = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PendingConflictStore(AppPaths paths) : this(new EngineDatabase(paths), paths) { }

    public static string MakeId(string jobName, string deviceName, string relativePath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{jobName}\n{deviceName}\n{relativePath}")))[..16].ToLowerInvariant();

    public async Task<IReadOnlyList<PendingConflict>> ListAsync(string? jobName = null, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        var rows = jobName is null
            ? await Sql.QueryAsync(connection, null, "SELECT Json FROM PendingConflicts;", r => r.GetString(0), ct)
            : await Sql.QueryAsync(connection, null, "SELECT Json FROM PendingConflicts WHERE JobName = @job;", r => r.GetString(0), ct, ("@job", jobName));
        return rows
            .Select(json => JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.PendingConflict)!)
            .OrderBy(c => c.JobName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<PendingConflict?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        var rows = await Sql.QueryAsync(connection, null, "SELECT Json FROM PendingConflicts WHERE Id = @id;", r => r.GetString(0), ct, ("@id", id));
        return rows.Count == 0 ? null : JsonSerializer.Deserialize(rows[0], AppConfigJsonContext.Default.PendingConflict);
    }

    public async Task<int> ReplaceDeviceAsync(
        string jobName, string deviceName, IReadOnlyList<HeldConflict> held, string stagingPath, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await database.OpenAsync(ct);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            var existing = (await Sql.QueryAsync(connection, transaction,
                    "SELECT Json FROM PendingConflicts WHERE JobName = @job AND DeviceName = @device;", r => r.GetString(0), ct,
                    ("@job", jobName), ("@device", deviceName)))
                .Select(json => JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.PendingConflict)!)
                .ToList();
            var existingById = existing.ToDictionary(c => c.Id, StringComparer.Ordinal);
            var next = new List<PendingConflict>();
            var added = 0;

            foreach (var h in held)
            {
                var id = MakeId(jobName, deviceName, h.RelativePath);
                existingById.TryGetValue(id, out var previous);
                if (previous is null)
                    added++;

                var conflict = new PendingConflict
                {
                    Id = id,
                    JobName = jobName,
                    DeviceName = deviceName,
                    RelativePath = h.RelativePath,
                    DetectedAtUtc = previous?.DetectedAtUtc ?? DateTimeOffset.UtcNow,
                    DeviceSize = h.StagingSize,
                    DeviceModifiedUtc = h.StagingModifiedUtc,
                    MasterSize = h.MasterSize,
                    MasterModifiedUtc = h.MasterModifiedUtc,
                };

                var copyPath = GetCopyPath(conflict);
                if (h.StagingSize is null)
                {
                    DeleteQuietly(copyPath);
                }
                else if (!File.Exists(copyPath) || previous is null || previous.DeviceSize != h.StagingSize || !SameTime(previous.DeviceModifiedUtc, h.StagingModifiedUtc))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);
                    File.Copy(Path.Combine(stagingPath, h.RelativePath), copyPath, overwrite: true);
                }

                next.Add(conflict);
            }

            var keep = next.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in existing.Where(c => !keep.Contains(c.Id)))
                DeleteQuietly(GetCopyPath(gone));

            await Sql.ExecuteAsync(connection, transaction,
                "DELETE FROM PendingConflicts WHERE JobName = @job AND DeviceName = @device;", ct, ("@job", jobName), ("@device", deviceName));
            foreach (var conflict in next)
                await InsertAsync(connection, transaction, conflict, ct);
            await transaction.CommitAsync(ct);
            return added;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? GetDeviceCopyPath(PendingConflict conflict)
    {
        var path = GetCopyPath(conflict);
        return File.Exists(path) ? path : null;
    }

    public async Task RemoveAsync(PendingConflict conflict, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await database.OpenAsync(ct);
            await Sql.ExecuteAsync(connection, null, "DELETE FROM PendingConflicts WHERE Id = @id;", ct, ("@id", conflict.Id));
            DeleteQuietly(GetCopyPath(conflict));
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction, PendingConflict conflict, CancellationToken ct) =>
        Sql.ExecuteAsync(connection, transaction, """
            INSERT INTO PendingConflicts (Id, JobName, DeviceName, Json) VALUES (@id, @job, @device, @json)
            ON CONFLICT (Id) DO UPDATE SET Json = excluded.Json;
            """, ct,
            ("@id", conflict.Id), ("@job", ConfigNames.Segment(conflict.JobName)), ("@device", ConfigNames.Segment(conflict.DeviceName)),
            ("@json", JsonSerializer.Serialize(conflict, AppConfigJsonContext.Default.PendingConflict)));

    private string GetCopyPath(PendingConflict conflict) =>
        Path.Combine(paths.PendingConflictsDir, ConfigNames.Segment(conflict.JobName), ConfigNames.Segment(conflict.DeviceName), $"{conflict.Id}.blob");

    private static bool SameTime(DateTimeOffset? a, DateTimeOffset? b) =>
        a is not null && b is not null && (a.Value - b.Value).Duration() <= ModifiedTolerance;

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
