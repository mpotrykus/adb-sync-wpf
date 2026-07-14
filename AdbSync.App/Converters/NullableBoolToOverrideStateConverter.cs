using System.Globalization;
using System.Windows.Data;

namespace AdbSync.App.Converters;

/// <summary>Maps a tri-state override checkbox's IsChecked to its plain-language state: null = inherit.</summary>
public sealed class NullableBoolToOverrideStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        true => "On",
        false => "Off",
        _ => "Inherit",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
