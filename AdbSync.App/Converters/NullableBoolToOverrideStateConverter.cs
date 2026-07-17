using System.Globalization;
using System.Windows.Data;

namespace AdbSync.App.Converters;

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
