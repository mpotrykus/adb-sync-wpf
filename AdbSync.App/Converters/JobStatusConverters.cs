using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AdbSync.App.Converters;

/// <summary>Maps a job's PhaseText ("Idle" vs an active SyncPhase name) to a status-dot/pill brush.</summary>
public sealed class PhaseToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && text != "Idle"
            ? Application.Current.Resources["Brush.Accent"]
            : Application.Current.Resources["Brush.Text.Secondary"];

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a job's LastOutcome text to a semantic brush based on its "Error:"/"Skipped:" prefix.</summary>
public sealed class OutcomeToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            string s when s.StartsWith("Error:", StringComparison.Ordinal) => "Brush.Danger",
            string s when s.StartsWith("Skipped:", StringComparison.Ordinal) => "Brush.Warning",
            string s when s.Length > 0 => "Brush.Success",
            _ => "Brush.Text.Secondary",
        };
        return Application.Current.Resources[key];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
