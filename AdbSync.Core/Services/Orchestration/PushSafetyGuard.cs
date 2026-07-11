using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;

namespace AdbSync.Core.Services.Orchestration;

public sealed class PushSafetyGuard(AppPaths paths) : IPushSafetyGuard
{
    private const double MinimumFraction = 0.25;

    public async Task RecordDeviceSnapshotAsync(string jobName, string deviceName, int fileCount, CancellationToken ct = default)
    {
        var history = await LoadHistoryAsync(jobName, ct);
        if (!history.MaxFileCounts.TryGetValue(deviceName, out var existing) || fileCount > existing)
        {
            history.MaxFileCounts[deviceName] = fileCount;
            await SaveHistoryAsync(jobName, history, ct);
        }
    }

    public async Task AssertSafeToPushAsync(string jobName, string masterPath, CancellationToken ct = default)
    {
        var masterCount = CountFiles(masterPath);
        if (masterCount == 0)
            throw new PushSafetyException($"Safety check blocked push for '{jobName}': master folder is empty.");

        var history = await LoadHistoryAsync(jobName, ct);
        if (history.MaxFileCounts.Count == 0)
            return;

        var maxHistorical = history.MaxFileCounts.Values.Max();
        var minimumAllowed = Math.Max(1, (int)Math.Floor(maxHistorical * MinimumFraction));
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

    private async Task<PushSafetyHistory> LoadHistoryAsync(string jobName, CancellationToken ct) =>
        await JsonFileIo.ReadAsync(GetHistoryPath(jobName), AppConfigJsonContext.Default.PushSafetyHistory, ct) ?? new PushSafetyHistory();

    private Task SaveHistoryAsync(string jobName, PushSafetyHistory history, CancellationToken ct) =>
        JsonFileIo.WriteAtomicAsync(GetHistoryPath(jobName), history, AppConfigJsonContext.Default.PushSafetyHistory, ct);

    private string GetHistoryPath(string jobName) => Path.Combine(paths.Root, "push-safety", $"{jobName}.json");
}
