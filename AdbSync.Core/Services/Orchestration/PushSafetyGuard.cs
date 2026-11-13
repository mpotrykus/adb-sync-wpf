using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Common;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Persistence;
using System.Text.Json;

namespace AdbSync.Core.Services.Orchestration;

public sealed class PushSafetyGuard(EngineDatabase database) : IPushSafetyGuard
{
    public PushSafetyGuard(AppPaths paths) : this(new EngineDatabase(paths)) { }

    public async Task RecordDeviceSnapshotAsync(string jobName, string deviceName, int fileCount, CancellationToken ct = default)
    {
        var history = await LoadHistoryAsync(jobName, ct);
        if (!history.MaxFileCounts.TryGetValue(deviceName, out var existing) || fileCount > existing)
        {
            history.MaxFileCounts[deviceName] = fileCount;
            await SaveHistoryAsync(jobName, history, ct);
        }
    }

    public async Task AssertSafeToPushAsync(string jobName, string masterPath, int minimumPercent, CancellationToken ct = default)
    {
        var masterCount = CountFiles(masterPath);
        if (masterCount == 0)
            throw new PushSafetyException($"Safety check blocked push for '{jobName}': master folder is empty.");

        var history = await LoadHistoryAsync(jobName, ct);
        if (history.MaxFileCounts.Count == 0)
            return;

        var maxHistorical = history.MaxFileCounts.Values.Max();
        var minimumAllowed = Math.Max(1, (int)Math.Floor(maxHistorical * (minimumPercent / 100.0)));
        if (masterCount < minimumAllowed)
        {
            throw new PushSafetyException(
                $"Safety check blocked push for '{jobName}': master file count ({masterCount}) is far below historical baseline ({maxHistorical}).");
        }
    }

    public async Task ForcePushAsync(string jobName, string masterPath, CancellationToken ct = default)
    {
        var history = await LoadHistoryAsync(jobName, ct);
        if (history.MaxFileCounts.Count == 0)
            return;

        var masterCount = CountFiles(masterPath);
        foreach (var device in history.MaxFileCounts.Keys.ToList())
            history.MaxFileCounts[device] = masterCount;
        await SaveHistoryAsync(jobName, history, ct);
    }

    private static int CountFiles(string path) =>
        Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count() : 0;

    private async Task<PushSafetyHistory> LoadHistoryAsync(string jobName, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        var rows = await Sql.QueryAsync(connection, null, "SELECT Json FROM PushSafety WHERE JobName = @job;", r => r.GetString(0), ct, ("@job", jobName));
        return rows.Count == 0 ? new PushSafetyHistory() : JsonSerializer.Deserialize(rows[0], AppConfigJsonContext.Default.PushSafetyHistory)!;
    }

    private async Task SaveHistoryAsync(string jobName, PushSafetyHistory history, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await Sql.ExecuteAsync(connection, null, """
            INSERT INTO PushSafety (JobName, Json) VALUES (@job, @json)
            ON CONFLICT (JobName) DO UPDATE SET Json = excluded.Json;
            """, ct, ("@job", ConfigNames.Segment(jobName)), ("@json", JsonSerializer.Serialize(history, AppConfigJsonContext.Default.PushSafetyHistory)));
    }
}
