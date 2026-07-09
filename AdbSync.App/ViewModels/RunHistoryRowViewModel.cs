using AdbSync.App.Converters;
using AdbSync.Core.Orchestration;
using AdbSync.Core.Orchestration.RunHistory;

namespace AdbSync.App.ViewModels;

public sealed class RunHistoryRowViewModel(JobRunRecord record)
{
    public Guid RunId { get; } = record.RunId;
    public string StartedAtText { get; } = record.StartedAt.LocalDateTime.ToString("g");
    public string DurationText { get; } = FormatDuration(record.CompletedAt - record.StartedAt);
    public string PullDurationText { get; } = record.PullDuration is { } pull ? FormatDuration(pull) : "-";
    public string PushDurationText { get; } = record.PushDuration is { } push ? FormatDuration(push) : "-";
    public int FilesCopied { get; } = record.FilesCopied;
    public int FilesDeleted { get; } = record.FilesDeleted;
    public string SizeText { get; } = FormatSize(record.BytesCopied);
    public int ErrorCount { get; } = record.ErrorCount;
    public JobRunOutcome Outcome { get; } = record.Outcome;
    public string OutcomeText { get; } = RunOutcomeDisplay.FriendlyName(record.Outcome);
    public string? ErrorMessage { get; } = record.ErrorMessage;

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            return "-";
        }

        var totalSeconds = (long)duration.TotalSeconds;
        var days = totalSeconds / 86400;
        var hours = totalSeconds % 86400 / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;

        if (days > 0)
        {
            return $"{days}d {hours}h {minutes}m {seconds}s";
        }
        if (hours > 0)
        {
            return $"{hours}h {minutes}m {seconds}s";
        }
        if (minutes > 0)
        {
            return $"{minutes}m {seconds}s";
        }
        return $"{seconds}s";
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return unitIndex == 0 ? $"{size:0} {units[unitIndex]}" : $"{size:0.#} {units[unitIndex]}";
    }
}
