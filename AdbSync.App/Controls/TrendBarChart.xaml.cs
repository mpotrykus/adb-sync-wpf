using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AdbSync.App.Controls;

public sealed record TrendBarChartPoint(double Value, Brush Brush, string Tooltip);

/// <summary>Minimal bottom-anchored bar chart, hand-drawn on a Canvas since the app takes no charting dependency.</summary>
public partial class TrendBarChart : UserControl
{
    private const double TopHeadroom = 12;
    private const double BarGap = 4;
    private const double MaxBarWidth = 36;

    private IReadOnlyList<TrendBarChartPoint> _points = [];

    public TrendBarChart()
    {
        InitializeComponent();
    }

    public void SetData(IReadOnlyList<TrendBarChartPoint> points)
    {
        _points = points;
        Redraw();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        BarsCanvas.Children.Clear();

        var width = ActualWidth;
        var height = ActualHeight;
        if (_points.Count == 0 || width <= 0 || height <= 0)
        {
            EmptyText.Visibility = _points.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        EmptyText.Visibility = Visibility.Collapsed;

        var max = _points.Max(p => p.Value);
        if (max <= 0)
            max = 1;

        var slot = width / _points.Count;
        var barWidth = Math.Clamp(slot - BarGap, 2, MaxBarWidth);
        var usableHeight = Math.Max(height - TopHeadroom, 1);

        for (var i = 0; i < _points.Count; i++)
        {
            var point = _points[i];
            var barHeight = Math.Max(point.Value / max * usableHeight, 2);
            var rect = new Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                Fill = point.Brush,
                RadiusX = Math.Min(3, barWidth / 2),
                RadiusY = Math.Min(3, barWidth / 2),
                ToolTip = point.Tooltip,
                SnapsToDevicePixels = true,
            };
            var left = i * slot + (slot - barWidth) / 2;
            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, height - barHeight);
            BarsCanvas.Children.Add(rect);
        }
    }
}
