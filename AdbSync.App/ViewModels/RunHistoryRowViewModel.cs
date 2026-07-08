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
    public string FilesText { get; } = $"{record.FilesCopied} copied, {record.FilesDeleted} deleted";
    public string SizeText { get; } = FormatSize(record.BytesCopied);
    public int ErrorCount { get; } = record.ErrorCount;
    public JobRunOutcome Outcome { get; } = record.Outcome;
    public string OutcomeText { get; } = RunOutcomeDisplay.FriendlyName(record.Outcome);
    public string? ErrorMessage { get; } = record.ErrorMessage;

    internal static string FormatDuration(TimeSpan duration) =>
        duration < TimeSpan.Zero ? "-" : duration.ToString(duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");

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
