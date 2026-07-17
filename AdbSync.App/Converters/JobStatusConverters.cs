using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AdbSync.App.Converters;

public sealed class PhaseToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && text != "Idle"
            ? Application.Current.Resources["Brush.Accent"]
            : Application.Current.Resources["Brush.Text.Secondary"];

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class OutcomeToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            string s when s.StartsWith("Error:", StringComparison.Ordinal) => "Brush.Danger",
            string s when s.StartsWith("Skipped:", StringComparison.Ordinal) => "Brush.Warning",
            string s when s.StartsWith("Stopped", StringComparison.Ordinal) => "Brush.Warning",
            string s when s.Length > 0 => "Brush.Success",
            _ => "Brush.Text.Secondary",
        };
        return Application.Current.Resources[key];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
