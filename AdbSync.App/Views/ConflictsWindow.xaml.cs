using AdbSync.App.Services;
using AdbSync.Core.Models.Merge;
using AdbSync.Core.Services.Merge;
using System.Windows;
using System.Windows.Controls;

namespace AdbSync.App.Views;

public partial class ConflictsWindow : Window
{
    private readonly IPendingConflictStore _store;
    private readonly IConflictResolver _resolver;
    private readonly AppConfigService _configService;
    private readonly JobRunService _jobRunService;
    private readonly string? _jobName;
    private bool _busy;

    public ConflictsWindow(
        IPendingConflictStore store, IConflictResolver resolver, AppConfigService configService, JobRunService jobRunService, string? jobName)
    {
        InitializeComponent();
        _store = store;
        _resolver = resolver;
        _configService = configService;
        _jobRunService = jobRunService;
        _jobName = jobName;
        Title = jobName is null ? "Resolve Conflicts" : $"Resolve Conflicts - {jobName}";
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var conflicts = await _store.ListAsync(_jobName);
        HeaderText.Text = conflicts.Count == 1 ? "1 file needs a decision" : $"{conflicts.Count} files need a decision";
        BulkPanel.Visibility = conflicts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = conflicts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ItemsPanel.Children.Clear();
        var rows = new List<UIElement>();
        foreach (var conflict in conflicts)
            rows.Add(BuildRow(conflict, await _resolver.GetFilesAsync(conflict.Id)));
        ItemsPanel.Children.Clear();
        foreach (var row in rows)
            ItemsPanel.Children.Add(row);
    }

    private UIElement BuildRow(PendingConflict c, ConflictFiles? files)
    {
        var card = new Border { Style = (Style)FindResource("CardBorder"), Padding = new Thickness(14, 12, 14, 14), Margin = new Thickness(0, 0, 0, 10) };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = c.RelativePath, Style = (Style)FindResource("SectionLabel"), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = c.JobName, Style = (Style)FindResource("CaptionText"), Margin = new Thickness(0, 2, 0, 12) });

        var deviceNewer = c.DeviceModifiedUtc is not null && (c.MasterModifiedUtc is null || c.DeviceModifiedUtc >= c.MasterModifiedUtc);
        var sides = new Grid();
        sides.ColumnDefinitions.Add(new ColumnDefinition());
        sides.ColumnDefinitions.Add(new ColumnDefinition());
        var pcSide = SideColumn("PC", "Keep PC's", c, files?.MasterPath, c.MasterSize, c.MasterModifiedUtc, !deviceNewer, ConflictChoice.Master);
        var deviceSide = SideColumn(c.DeviceName, $"Keep {c.DeviceName}'s", c, files?.DeviceCopyPath, c.DeviceSize, c.DeviceModifiedUtc, deviceNewer, ConflictChoice.Device);
        pcSide.Margin = new Thickness(0, 0, 6, 0);
        deviceSide.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(deviceSide, 1);
        sides.Children.Add(pcSide);
        sides.Children.Add(deviceSide);
        stack.Children.Add(sides);

        if (c.DeviceModifiedUtc is not null && c.MasterModifiedUtc is not null)
        {
            var both = ChoiceButton("Keep both", c, ConflictChoice.Both);
            both.Margin = new Thickness(0, 12, 0, 0);
            both.HorizontalAlignment = HorizontalAlignment.Stretch;
            stack.Children.Add(both);
        }

        card.Child = stack;
        return card;
    }

    private StackPanel SideColumn(
        string label, string buttonText, PendingConflict c, string? path, long? size, DateTimeOffset? modified, bool newer, ConflictChoice choice)
    {
        var column = new StackPanel();
        var secondary = (System.Windows.Media.Brush)FindResource("Brush.Text.Secondary");

        var thumb = path is null ? null : ThumbnailRenderer.Load(path, 360, c.RelativePath);
        FrameworkElement visual = thumb is not null
            ? new Image { Source = thumb, Stretch = System.Windows.Media.Stretch.Uniform, MaxHeight = 180, HorizontalAlignment = HorizontalAlignment.Center }
            : new TextBlock
            {
                Text = modified is null ? "\uE74D" : "\uE8A5",
                FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 56,
                Foreground = secondary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        column.Children.Add(new Border
        {
            Height = 180,
            CornerRadius = new CornerRadius(6),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            Child = visual,
        });

        column.Children.Add(new TextBlock
        {
            Text = newer ? $"{label} (newer)" : label,
            Style = (Style)FindResource("SectionLabel"),
            Margin = new Thickness(0, 10, 0, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        column.Children.Add(new TextBlock
        {
            Text = modified is null ? "Deleted" : $"{FormatSize(size)}\nModified {modified.Value.ToLocalTime():g}",
            Foreground = secondary,
        });

        var keep = ChoiceButton(buttonText, c, choice);
        keep.Margin = new Thickness(0, 10, 0, 0);
        keep.HorizontalAlignment = HorizontalAlignment.Stretch;
        column.Children.Add(keep);
        return column;
    }

    private Button ChoiceButton(string text, PendingConflict c, ConflictChoice choice)
    {
        var button = new Button { Content = text, Style = (Style)FindResource("RowButton"), Padding = new Thickness(12, 8, 12, 8), MinHeight = 38 };
        button.Click += async (_, _) => await ResolveAsync([c], choice);
        return button;
    }

    private async void KeepNewestAll_Click(object sender, RoutedEventArgs e) => await ResolveAsync(await _store.ListAsync(_jobName), ConflictChoice.Newest);

    private async void KeepPcAll_Click(object sender, RoutedEventArgs e) => await ResolveAsync(await _store.ListAsync(_jobName), ConflictChoice.Master);

    private async Task ResolveAsync(IReadOnlyList<PendingConflict> conflicts, ConflictChoice choice)
    {
        if (_busy)
            return;
        _busy = true;
        IsEnabled = false;
        try
        {
            var jobs = new HashSet<string>(StringComparer.Ordinal);
            var failures = new List<string>();
            foreach (var c in conflicts)
            {
                try
                {
                    jobs.Add(await _resolver.ResolveAsync(c.Id, choice));
                }
                catch (ConflictResolutionException ex)
                {
                    failures.Add($"{c.RelativePath}: {ex.Message}");
                }
            }

            var config = await _configService.GetAsync();
            foreach (var job in jobs)
            {
                var index = config.Jobs.FindIndex(j => j.Name == job);
                if (index >= 0)
                    _ = _jobRunService.RunJobAsync(index);
            }

            if (failures.Count > 0)
                ConfirmDialog.Show(this, "Some conflicts weren't resolved", string.Join(Environment.NewLine, failures.Take(5)), confirmText: "OK");
        }
        finally
        {
            _busy = false;
            IsEnabled = true;
            await RefreshAsync();
        }
    }

    private static string FormatSize(long? size) => size switch
    {
        null => "?",
        < 1024 => $"{size} B",
        < 1024 * 1024 => $"{size / 1024.0:0.#} KB",
        _ => $"{size / 1024.0 / 1024.0:0.#} MB",
    };
}
