using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AdbSync.App.Controls;

public sealed record DonutSegment(double Value, Brush Brush, string Tooltip);

/// <summary>Minimal donut chart, hand-drawn on a Canvas since the app takes no charting dependency.</summary>
public partial class OutcomeDonutChart : UserControl
{
    private const double Thickness = 14;
    private const double GapDegrees = 2;

    private IReadOnlyList<DonutSegment> _segments = [];

    public OutcomeDonutChart()
    {
        InitializeComponent();
    }

    public void SetSegments(IReadOnlyList<DonutSegment> segments, string centerValue, string centerLabel)
    {
        _segments = segments.Where(s => s.Value > 0).ToList();
        CenterValueText.Text = centerValue;
        CenterLabelText.Text = centerLabel;
        Redraw();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        RingCanvas.Children.Clear();

        var size = Math.Min(ActualWidth, ActualHeight);
        var total = _segments.Sum(s => s.Value);
        if (size <= 0 || total <= 0)
        {
            EmptyText.Visibility = Visibility.Visible;
            CenterPanel.Visibility = Visibility.Collapsed;
            return;
        }
        EmptyText.Visibility = Visibility.Collapsed;
        CenterPanel.Visibility = Visibility.Visible;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var outerRadius = size / 2;
        var innerRadius = outerRadius - Thickness;

        // A single fully-populated segment can't be drawn as one arc (zero-length start/end seam), so it's
        // split into two half-rings of the same brush - visually identical, but avoids the degenerate geometry.
        var slices = _segments.Count == 1
            ? [_segments[0] with { Value = _segments[0].Value / 2 }, _segments[0]]
            : _segments;

        double startAngle = 0;
        foreach (var slice in slices)
        {
            var sweep = slice.Value / total * 360;
            var endAngle = startAngle + sweep;
            var gapped = sweep > GapDegrees && slices.Count > 1;
            var path = new Path
            {
                Fill = slice.Brush,
                ToolTip = slice.Tooltip,
                Data = BuildSlice(center, outerRadius, innerRadius,
                    gapped ? startAngle + GapDegrees / 2 : startAngle,
                    gapped ? endAngle - GapDegrees / 2 : endAngle),
            };
            RingCanvas.Children.Add(path);
            startAngle = endAngle;
        }
    }

    private static Geometry BuildSlice(Point center, double outerRadius, double innerRadius, double startDeg, double endDeg)
    {
        var start = ToRadians(startDeg);
        var end = ToRadians(endDeg);

        var outerStart = PointOnCircle(center, outerRadius, start);
        var outerEnd = PointOnCircle(center, outerRadius, end);
        var innerEnd = PointOnCircle(center, innerRadius, end);
        var innerStart = PointOnCircle(center, innerRadius, start);
        var isLargeArc = endDeg - startDeg > 180;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(outerStart, isFilled: true, isClosed: true);
            ctx.ArcTo(outerEnd, new Size(outerRadius, outerRadius), 0, isLargeArc, SweepDirection.Clockwise, true, true);
            ctx.LineTo(innerEnd, true, true);
            ctx.ArcTo(innerStart, new Size(innerRadius, innerRadius), 0, isLargeArc, SweepDirection.Counterclockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

    private static Point PointOnCircle(Point center, double radius, double angleRad) =>
        new(center.X + radius * Math.Sin(angleRad), center.Y - radius * Math.Cos(angleRad));
}
