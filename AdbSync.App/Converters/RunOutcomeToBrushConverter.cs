using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;

namespace AdbSync.App.Converters;

/// <summary>Maps a run's outcome to the semantic brush and display name used across the run-history grid, legend and charts.</summary>
public static class RunOutcomeDisplay
{
    public static Brush Resolve(JobRunOutcome outcome)
    {
        var key = outcome switch
        {
            JobRunOutcome.Completed => "Brush.Success",
            JobRunOutcome.CompletedNoChanges => "Brush.Accent",
            JobRunOutcome.Skipped or JobRunOutcome.SkippedAppRunning => "Brush.Warning",
            JobRunOutcome.Failed => "Brush.Danger",
            JobRunOutcome.DryRunCompleted => "Brush.Accent",
            JobRunOutcome.Cancelled => "Brush.Warning",
            _ => "Brush.Text.Secondary",
        };
        return (Brush)Application.Current.Resources[key];
    }

    public static string FriendlyName(JobRunOutcome outcome) => outcome switch
    {
        JobRunOutcome.Completed => "Completed",
        JobRunOutcome.CompletedNoChanges => "No changes",
        JobRunOutcome.Skipped => "Skipped",
        JobRunOutcome.SkippedAppRunning => "App running",
        JobRunOutcome.Failed => "Failed",
        JobRunOutcome.DryRunCompleted => "Dry run",
        JobRunOutcome.Cancelled => "Stopped",
        _ => outcome.ToString(),
    };
}

public sealed class RunOutcomeToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is JobRunOutcome outcome ? RunOutcomeDisplay.Resolve(outcome) : RunOutcomeDisplay.Resolve(default);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
