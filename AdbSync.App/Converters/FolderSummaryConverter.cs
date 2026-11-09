using AdbSync.App.ViewModels;
using System.Globalization;
using System.Windows.Data;

namespace AdbSync.App.Converters;

/// <summary>Summarizes a folder group's rows by action, e.g. "120 Copied · 3 Deleted".</summary>
public sealed class FolderSummaryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<object> items
            ? string.Join(" · ", items.OfType<RunFileRowViewModel>()
                .GroupBy(r => r.ActionText)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count():N0} {g.Key}"))
            : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
