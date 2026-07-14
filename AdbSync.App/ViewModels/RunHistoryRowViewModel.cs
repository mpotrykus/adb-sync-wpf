using AdbSync.App.Converters;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using AdbSync.Core.Services.Orchestration.RunHistory;

namespace AdbSync.App.ViewModels;

public sealed class RunHistoryRowViewModel
{
    public RunHistoryRowViewModel(JobRunRecord record)
    {
        RunId = record.RunId;
        StartedAtText = record.StartedAt.LocalDateTime.ToString("g");
        DurationText = FormatDuration(record.CompletedAt - record.StartedAt);
        PullDurationText = record.PullDuration is { } pull ? FormatDuration(pull) : "-";
        PushDurationText = record.PushDuration is { } push ? FormatDuration(push) : "-";
        FilesCopied = record.FilesCopied;
        FilesDeleted = record.FilesDeleted;
        SizeText = FormatSize(record.BytesCopied);
        ErrorCount = record.ErrorCount;
        Outcome = record.Outcome;
        OutcomeText = RunOutcomeDisplay.FriendlyName(record.Outcome);
        ErrorMessage = record.ErrorMessage;
    }

    /// <summary>Builds the synthetic row shown at the top of the grid for a job that's currently running - there's
    /// no <see cref="JobRunRecord"/> yet, so the stats columns are placeholders until the real row lands.</summary>
    public RunHistoryRowViewModel(string phaseText, DateTimeOffset startedAt)
    {
        IsRunning = true;
        RunId = Guid.Empty;
        StartedAtText = startedAt.LocalDateTime.ToString("g");
        DurationText = FormatDuration(DateTimeOffset.Now - startedAt);
        PullDurationText = "-";
        PushDurationText = "-";
        SizeText = "-";
        OutcomeText = phaseText;
    }

    /// <summary>True for the synthetic in-progress row - also drives the shared running-shimmer <c>DataGridRow</c>
    /// style in Theme.xaml, which binds to this same property name on the dashboard's job rows.</summary>
    public bool IsRunning { get; }
    public Guid RunId { get; }
    public string StartedAtText { get; }
    public string DurationText { get; }
    public string PullDurationText { get; }
    public string PushDurationText { get; }
    public int FilesCopied { get; }
    public int FilesDeleted { get; }
    public string SizeText { get; }
    public int ErrorCount { get; }
    public JobRunOutcome Outcome { get; }
    public string OutcomeText { get; }
    public string? ErrorMessage { get; }

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
