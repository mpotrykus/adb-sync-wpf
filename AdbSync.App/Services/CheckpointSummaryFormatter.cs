using AdbSync.App.ViewModels;
using AdbSync.Core.Models.Orchestration;

namespace AdbSync.App.Services;

public static class CheckpointSummaryFormatter
{
    public static string Format(SyncCheckpoint checkpoint, int totalDevices)
    {
        var where = totalDevices > 0
            ? $"{checkpoint.Phase} ({checkpoint.CompletedDevices.Count}/{totalDevices} devices done)"
            : checkpoint.Phase.ToString();
        return $"Interrupted during {where} - saved {JobStatusViewModel.FormatRelative(checkpoint.SavedAt)}";
    }
}
