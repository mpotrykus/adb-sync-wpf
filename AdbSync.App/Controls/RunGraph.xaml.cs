using AdbSync.App.ViewModels;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AdbSync.App.Controls;

/// <summary>What the user picked in the graph: a whole device row (<see cref="Phase"/> null) or one step node.</summary>
public sealed record RunGraphSelection(string DeviceName, SyncPhase? Phase);

/// <summary>Live execution graph of one run: a row per device, a column per phase, nodes that restyle in place as
/// the run progresses. Hand-drawn on a Canvas, like the other charts, since the app takes no graphing dependency.</summary>
public partial class RunGraph : UserControl
{
    private const double NodeWidth = 142;
    private const double NodeHeight = 52;
    private const double ColumnGap = 46;
    private const double RowGap = 10;
    private const double LabelWidth = 112;
    private const double HeaderHeight = 22;
    private const double Pad = 8;

    private static readonly TimeSpan ColorTween = TimeSpan.FromMilliseconds(300);

    private sealed class NodeView
    {
        public required string Device { get; init; }
        public required SyncPhase Phase { get; init; }
        public required Border Root { get; init; }
        public required TextBlock Glyph { get; init; }
        public required TextBlock Detail { get; init; }
        public required SolidColorBrush Fill { get; init; }
        public required SolidColorBrush Stroke { get; init; }
        public RunStepStatus Status { get; set; }
        public RunStepRecord Step { get; set; } = null!;
        public bool Shimmering { get; set; }
    }

    private sealed record EdgeView(NodeView From, NodeView To, Path Line);

    private readonly Dictionary<(string, SyncPhase), NodeView> _nodes = new();
    private readonly List<EdgeView> _edges = [];
    private readonly List<string> _devices = [];
    private readonly Dictionary<string, TextBlock> _rowLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _layoutKey = "";
    private RunGraphSelection? _selection;

    public RunGraph()
    {
        InitializeComponent();
        _elapsedTimer.Tick += (_, _) => RefreshElapsed();
        Loaded += (_, _) => _elapsedTimer.Start();
        Unloaded += (_, _) => _elapsedTimer.Stop();
    }

    public event Action<RunGraphSelection?>? SelectionChanged;

    public RunGraphSelection? Selection => _selection;

    public void SetEmpty(string message)
    {
        Clear();
        EmptyText.Text = message;
        EmptyText.Visibility = Visibility.Visible;
    }

    public void Clear()
    {
        GraphCanvas.Children.Clear();
        GraphCanvas.Width = GraphCanvas.Height = 0;
        _nodes.Clear();
        _edges.Clear();
        _devices.Clear();
        _rowLabels.Clear();
        _layoutKey = "";
        EmptyText.Visibility = Visibility.Collapsed;
        if (_selection is not null)
            SetSelection(null, raise: true);
    }

    public void SetSteps(IReadOnlyList<RunStepRecord> steps)
    {
        if (steps.Count == 0)
        {
            SetEmpty("No step data was recorded for this run.");
            return;
        }
        EmptyText.Visibility = Visibility.Collapsed;

        var key = string.Join("|", steps.Select(s => $"{s.DeviceName}/{s.Phase}"));
        if (key != _layoutKey)
            Rebuild(steps, key);

        foreach (var step in steps)
        {
            if (_nodes.TryGetValue((step.DeviceName, step.Phase), out var node))
                Apply(node, step);
        }
        RestyleEdges();
        ApplySelectionStyles();
    }

    private void Rebuild(IReadOnlyList<RunStepRecord> steps, string key)
    {
        var selection = _selection;
        GraphCanvas.Children.Clear();
        _nodes.Clear();
        _edges.Clear();
        _devices.Clear();
        _rowLabels.Clear();
        _layoutKey = key;

        _devices.AddRange(steps.Select(s => s.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase));
        var phases = steps.Select(s => s.Phase).Distinct().OrderBy(PhaseOrder).ToList();

        for (var column = 0; column < phases.Count; column++)
        {
            var header = new TextBlock
            {
                Text = PhaseTitle(phases[column]).ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Brush.Text.Secondary"),
                Width = NodeWidth,
                TextAlignment = TextAlignment.Center,
            };
            Canvas.SetLeft(header, ColumnX(column));
            Canvas.SetTop(header, Pad);
            GraphCanvas.Children.Add(header);
        }

        for (var row = 0; row < _devices.Count; row++)
        {
            var device = _devices[row];
            var label = new TextBlock
            {
                Text = device,
                Width = LabelWidth - 10,
                Height = NodeHeight,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("Brush.Text.Primary"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Padding = new Thickness(0, 18, 0, 0),
                Cursor = Cursors.Hand,
                ToolTip = device,
                Background = Brushes.Transparent,
            };
            label.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Toggle(new RunGraphSelection(device, null));
            };
            Canvas.SetLeft(label, Pad);
            Canvas.SetTop(label, RowY(row));
            GraphCanvas.Children.Add(label);
            _rowLabels[device] = label;
        }

        foreach (var step in steps)
        {
            var row = _devices.FindIndex(d => SameDevice(d, step.DeviceName));
            var column = phases.IndexOf(step.Phase);
            var node = CreateNode(step);
            Canvas.SetLeft(node.Root, ColumnX(column));
            Canvas.SetTop(node.Root, RowY(row));
            Panel.SetZIndex(node.Root, 2);
            GraphCanvas.Children.Add(node.Root);
            _nodes[(step.DeviceName, step.Phase)] = node;
            AnimateIn(node.Root, column, row);
        }

        BuildEdges(phases);

        GraphCanvas.Width = ColumnX(phases.Count - 1) + NodeWidth + Pad * 2;
        GraphCanvas.Height = RowY(_devices.Count - 1) + NodeHeight + Pad * 2;

        if (selection is not null && !SelectionStillValid(selection))
            SetSelection(null, raise: true);
    }

    private bool SelectionStillValid(RunGraphSelection selection) =>
        selection.Phase is { } phase ? _nodes.ContainsKey((selection.DeviceName, phase)) : _rowLabels.ContainsKey(selection.DeviceName);

    private void BuildEdges(List<SyncPhase> phases)
    {
        for (var i = 0; i + 1 < phases.Count; i++)
        {
            var from = phases[i];
            var to = phases[i + 1];
            foreach (var device in _devices)
            {
                if (_nodes.TryGetValue((device, from), out var a) && _nodes.TryGetValue((device, to), out var b))
                    AddEdge(a, b);
            }

            // Push only starts once every device has merged, so the merge column fans in to the push column.
            if (from == SyncPhase.Merge && to == SyncPhase.Push && _devices.Count > 1)
            {
                foreach (var source in _devices)
                {
                    foreach (var target in _devices.Where(t => !SameDevice(t, source)))
                    {
                        if (_nodes.TryGetValue((source, from), out var a) && _nodes.TryGetValue((target, to), out var b))
                            AddEdge(a, b);
                    }
                }
            }
        }
    }

    private void AddEdge(NodeView from, NodeView to)
    {
        var start = new Point(Canvas.GetLeft(from.Root) + NodeWidth, Canvas.GetTop(from.Root) + NodeHeight / 2);
        var end = new Point(Canvas.GetLeft(to.Root), Canvas.GetTop(to.Root) + NodeHeight / 2);
        var bend = (end.X - start.X) / 2;
        var figure = new PathFigure { StartPoint = start };
        figure.Segments.Add(new BezierSegment(new Point(start.X + bend, start.Y), new Point(end.X - bend, end.Y), end, true));
        var line = new Path
        {
            Data = new PathGeometry([figure]),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stroke = new SolidColorBrush(Colors.Transparent),
            IsHitTestVisible = false,
        };
        Panel.SetZIndex(line, 1);
        GraphCanvas.Children.Add(line);
        _edges.Add(new EdgeView(from, to, line));
    }

    private NodeView CreateNode(RunStepRecord step)
    {
        var fill = new SolidColorBrush(Colors.Transparent);
        var stroke = new SolidColorBrush(Colors.Transparent);
        var glyph = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold, Width = 18, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = PhaseTitle(step.Phase),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var detail = new TextBlock
        {
            FontSize = 10.5,
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(glyph);
        header.Children.Add(title);
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(header);
        body.Children.Add(detail);

        var root = new Border
        {
            Width = NodeWidth,
            Height = NodeHeight,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(10, 0, 8, 0),
            Background = fill,
            BorderBrush = stroke,
            Child = body,
            Cursor = Cursors.Hand,
            RenderTransform = new TranslateTransform(),
        };

        var node = new NodeView
        {
            Device = step.DeviceName,
            Phase = step.Phase,
            Root = root,
            Glyph = glyph,
            Detail = detail,
            Fill = fill,
            Stroke = stroke,
            Status = (RunStepStatus)(-1),
            Step = step,
        };
        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Toggle(new RunGraphSelection(node.Device, node.Phase));
        };
        return node;
    }

    private void Apply(NodeView node, RunStepRecord step)
    {
        var statusChanged = node.Status != step.Status;
        node.Step = step;
        node.Status = step.Status;

        var color = StatusColor(step.Status);
        var fillColor = Color.FromArgb(step.Status == RunStepStatus.Pending ? (byte)0x10 : (byte)0x26, color.R, color.G, color.B);
        var strokeColor = Color.FromArgb(step.Status is RunStepStatus.Pending or RunStepStatus.Skipped ? (byte)0x55 : (byte)0xCC, color.R, color.G, color.B);

        node.Glyph.Foreground = new SolidColorBrush(color);
        node.Glyph.Text = StatusGlyph(step.Status);
        node.Detail.Text = DetailText(step);
        node.Root.ToolTip = TooltipText(step);

        if (statusChanged)
        {
            Tween(node.Fill, fillColor);
            Tween(node.Stroke, strokeColor);
            SetShimmer(node, step.Status is RunStepStatus.Running);
        }
    }

    private static void Tween(SolidColorBrush brush, Color to)
    {
        if (brush.Color == Colors.Transparent)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = to;
            return;
        }
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, ColorTween));
    }

    private void SetShimmer(NodeView node, bool on)
    {
        if (on == node.Shimmering)
            return;
        node.Shimmering = on;

        if (!on)
        {
            node.Root.BorderBrush = node.Stroke;
            return;
        }

        var accent = ((SolidColorBrush)FindResource("Brush.Accent")).Color;
        var transform = new TranslateTransform();
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            RelativeTransform = transform,
        };
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x4D, accent.R, accent.G, accent.B), 0));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, accent.R, accent.G, accent.B), 0.5));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x4D, accent.R, accent.G, accent.B), 1));
        node.Root.BorderBrush = gradient;
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1, TimeSpan.FromSeconds(2.4))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void RestyleEdges()
    {
        var dim = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
        var success = ((SolidColorBrush)FindResource("Brush.Success")).Color;
        var accent = ((SolidColorBrush)FindResource("Brush.Accent")).Color;
        var danger = ((SolidColorBrush)FindResource("Brush.Danger")).Color;

        foreach (var edge in _edges)
        {
            var color = edge.From.Status switch
            {
                RunStepStatus.Succeeded => edge.To.Status is RunStepStatus.Running or RunStepStatus.Waiting
                    ? accent
                    : Color.FromArgb(0x99, success.R, success.G, success.B),
                RunStepStatus.Failed or RunStepStatus.Cancelled => Color.FromArgb(0x99, danger.R, danger.G, danger.B),
                _ => dim,
            };

            var touchesSelection = _selection is { } selection && IsConnectedToSelection(edge, selection);
            edge.Line.StrokeThickness = touchesSelection ? 3 : 2;
            if (touchesSelection)
                color = Color.FromArgb(0xFF, color.R, color.G, color.B);
            edge.Line.Opacity = _selection is null || touchesSelection ? 1 : 0.4;
            Tween((SolidColorBrush)edge.Line.Stroke, color);
        }
    }

    private static bool IsConnectedToSelection(EdgeView edge, RunGraphSelection selection) =>
        selection.Phase is { } phase
            ? (edge.From.Phase == phase && SameDevice(edge.From.Device, selection.DeviceName))
              || (edge.To.Phase == phase && SameDevice(edge.To.Device, selection.DeviceName))
            : SameDevice(edge.From.Device, selection.DeviceName) || SameDevice(edge.To.Device, selection.DeviceName);

    private static bool SameDevice(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void Toggle(RunGraphSelection selection) =>
        SetSelection(_selection == selection ? null : selection, raise: true);

    private void OnCanvasClicked(object sender, MouseButtonEventArgs e)
    {
        if (_selection is not null)
            SetSelection(null, raise: true);
    }

    private void SetSelection(RunGraphSelection? selection, bool raise)
    {
        _selection = selection;
        ApplySelectionStyles();
        RestyleEdges();
        if (raise)
            SelectionChanged?.Invoke(selection);
    }

    private void ApplySelectionStyles()
    {
        foreach (var node in _nodes.Values)
        {
            var selected = _selection is { } s && SameDevice(s.DeviceName, node.Device) && (s.Phase is null || s.Phase == node.Phase);
            node.Root.BorderThickness = new Thickness(selected && _selection!.Phase is not null ? 2.5 : 1.5);
            node.Root.Opacity = _selection is null || selected ? 1 : 0.55;
        }

        var primary = (Brush)FindResource("Brush.Text.Primary");
        var accent = (Brush)FindResource("Brush.Accent");
        foreach (var (device, label) in _rowLabels)
            label.Foreground = _selection is { Phase: null } s && SameDevice(s.DeviceName, device) ? accent : primary;
    }

    private void RefreshElapsed()
    {
        foreach (var node in _nodes.Values)
        {
            if (node.Status is RunStepStatus.Running)
                node.Detail.Text = DetailText(node.Step);
        }
    }

    private static void AnimateIn(Border root, int column, int row)
    {
        var delay = TimeSpan.FromMilliseconds(column * 80 + row * 20);
        root.Opacity = 0;
        root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)) { BeginTime = delay });
        ((TranslateTransform)root.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-15, 0, TimeSpan.FromMilliseconds(250)) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private static double ColumnX(int column) => Pad + LabelWidth + column * (NodeWidth + ColumnGap);
    private static double RowY(int row) => Pad + HeaderHeight + row * (NodeHeight + RowGap);

    private static int PhaseOrder(SyncPhase phase) => phase switch
    {
        SyncPhase.PreConnect => 0,
        SyncPhase.Pull => 1,
        SyncPhase.Merge => 2,
        SyncPhase.Push => 3,
        _ => 4,
    };

    private static string PhaseTitle(SyncPhase phase) => phase == SyncPhase.PreConnect ? "Connect" : phase.ToString();

    private static string StatusGlyph(RunStepStatus status) => status switch
    {
        RunStepStatus.Pending => "○",
        RunStepStatus.Waiting => "◔",
        RunStepStatus.Running => "◉",
        RunStepStatus.Succeeded => "✓",
        RunStepStatus.Failed => "✕",
        RunStepStatus.Skipped => "⊘",
        RunStepStatus.Cancelled => "■",
        _ => "",
    };

    private Color StatusColor(RunStepStatus status)
    {
        var key = status switch
        {
            RunStepStatus.Running => "Brush.Accent",
            RunStepStatus.Waiting => "Brush.Warning",
            RunStepStatus.Succeeded => "Brush.Success",
            RunStepStatus.Failed => "Brush.Danger",
            RunStepStatus.Cancelled => "Brush.Warning",
            _ => "Brush.Text.Secondary",
        };
        return ((SolidColorBrush)FindResource(key)).Color;
    }

    private static TimeSpan? Elapsed(RunStepRecord step) =>
        step.StartedAt is { } start ? (step.EndedAt ?? DateTimeOffset.Now) - start : null;

    private static string DetailText(RunStepRecord step)
    {
        var elapsed = Elapsed(step) is { } e ? RunHistoryRowViewModel.FormatDuration(e) : null;
        switch (step.Status)
        {
            case RunStepStatus.Pending:
                return "waiting its turn";
            case RunStepStatus.Skipped:
                return "skipped";
            case RunStepStatus.Waiting:
                return "waiting for device";
            case RunStepStatus.Running:
                return $"running · {elapsed ?? "0s"}";
        }

        var parts = new List<string>();
        var touched = step.FilesCopied + step.FilesDeleted;
        switch (step.Phase)
        {
            case SyncPhase.Pull or SyncPhase.Push:
                parts.Add($"{touched} file{(touched == 1 ? "" : "s")}");
                if (step.BytesCopied > 0)
                    parts.Add(RunHistoryRowViewModel.FormatSize(step.BytesCopied));
                break;
            case SyncPhase.Merge:
                parts.Add($"{touched} changed");
                if (step.Conflicts > 0)
                    parts.Add($"{step.Conflicts} conflict{(step.Conflicts == 1 ? "" : "s")}");
                break;
        }
        if (step.Errors > 0)
            parts.Add($"{step.Errors} error{(step.Errors == 1 ? "" : "s")}");
        if (elapsed is not null)
            parts.Add(elapsed);
        else if (step.Status == RunStepStatus.Cancelled)
            parts.Add("stopped");
        return parts.Count == 0 ? step.Status.ToString().ToLowerInvariant() : string.Join(" · ", parts);
    }

    private static string TooltipText(RunStepRecord step)
    {
        var lines = new List<string> { $"{PhaseTitle(step.Phase)} - {step.DeviceName}", step.Status.ToString() };
        if (step.StartedAt is { } start)
            lines.Add($"Started {start.LocalDateTime:T}");
        if (step.EndedAt is { } end)
            lines.Add($"Ended {end.LocalDateTime:T}");
        if (step.Phase is SyncPhase.Pull or SyncPhase.Push && step.Status is RunStepStatus.Succeeded or RunStepStatus.Failed)
            lines.Add($"{step.FilesCopied} copied, {step.FilesDeleted} deleted, {RunHistoryRowViewModel.FormatSize(step.BytesCopied)}");
        return string.Join("\n", lines);
    }
}
