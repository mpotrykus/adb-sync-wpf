using AdbSync.App.Controls;
using AdbSync.App.Converters;
using AdbSync.App.ViewModels;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Orchestration.RunHistory;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AdbSync.App.Views;

public partial class RunHistoryWindow : Window
{
    private static readonly TimeSpan IdleRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromSeconds(1);
    private const string DefaultGraphHint = "Click a node or device to filter the log and files";

    private readonly IRunHistoryStore _historyStore;
    private readonly string _jobName;
    private readonly DashboardViewModel _dashboard;
    private readonly ILiveRunLogSink _liveLog;
    private readonly ILiveRunStepSink _liveSteps;
    private readonly DispatcherTimer _autoRefreshTimer;

    private readonly Dictionary<Guid, IReadOnlyList<RunStepRecord>> _stepCache = new();
    private IReadOnlyList<RunStepRecord> _currentSteps = [];
    private string _fullLog = "";
    private bool _refreshing;
    private Guid? _shownRunId;
    private bool _shownWasLive;
    private int _selectionVersion;
    private DateTimeOffset? _runningStartedAt;
    private DateTimeOffset? _followStartedAt;

    private static bool s_topCollapsed;
    private static bool s_runsCollapsed;
    private static bool s_bottomCollapsed;
    private static bool s_groupByFolder;
    private GridLength _bottomHeight = new(200);

    private readonly Dictionary<Guid, IReadOnlyList<RunFileRowViewModel>> _fileCache = new();
    private IReadOnlyList<RunFileRowViewModel> _fileRows = [];
    private ICollectionView? _filesView;
    private Guid? _filesLoadedFor;

    public RunHistoryWindow(IRunHistoryStore historyStore, string jobName, DashboardViewModel dashboard, ILiveRunLogSink liveLog, ILiveRunStepSink liveSteps)
    {
        InitializeComponent();
        _historyStore = historyStore;
        _jobName = jobName;
        _dashboard = dashboard;
        _liveLog = liveLog;
        _liveSteps = liveSteps;
        Title = $"Run History - {jobName}";

        _autoRefreshTimer = new DispatcherTimer { Interval = IdleRefreshInterval };
        _autoRefreshTimer.Tick += async (_, _) => await RefreshAsync();

        ApplySectionLayout();
        GroupByFolderBox.IsChecked = s_groupByFolder;

        FileActionFilter.Items.Add("All actions");
        foreach (var action in Enum.GetValues<RunFileAction>())
            FileActionFilter.Items.Add(action.ToString());
        FileActionFilter.SelectedIndex = 0;

        Loaded += async (_, _) =>
        {
            _liveSteps.Changed += OnLiveStepsChanged;
            await RefreshAsync();
            _autoRefreshTimer.Start();
        };
        Closed += (_, _) =>
        {
            _autoRefreshTimer.Stop();
            _liveSteps.Changed -= OnLiveStepsChanged;
        };
    }

    private async Task RefreshAsync()
    {
        var previous = RunsGrid.SelectedItem as RunHistoryRowViewModel;
        var selectedRunId = previous?.RunId;
        if (previous is { IsRunning: true })
            _followStartedAt = _runningStartedAt;

        var runs = await _historyStore.ListRunsAsync(_jobName);
        var rows = runs.Select(r => new RunHistoryRowViewModel(r)).ToList();

        var runningJob = _dashboard.Jobs.FirstOrDefault(j => j.Name == _jobName);
        var isRunning = runningJob?.IsRunning ?? false;
        _runningStartedAt = null;
        if (isRunning && _liveLog.TryGet(_jobName, out var startedAt, out _))
        {
            _runningStartedAt = startedAt;
            rows.Insert(0, new RunHistoryRowViewModel(runningJob!.PhaseText, startedAt));
        }
        _autoRefreshTimer.Interval = isRunning ? LiveRefreshInterval : IdleRefreshInterval;

        RunHistoryRowViewModel? target = null;
        var followed = false;
        if (selectedRunId is not null)
            target = rows.FirstOrDefault(r => r.RunId == selectedRunId);

        if (target is null && _followStartedAt is { } follow)
        {
            var finished = runs.FirstOrDefault(r => r.StartedAt >= follow.AddSeconds(-5));
            if (finished is not null)
            {
                target = rows.FirstOrDefault(r => r.RunId == finished.RunId);
                followed = target is not null;
                _followStartedAt = null;
            }
        }

        _refreshing = true;
        try
        {
            RunsGrid.ItemsSource = rows;
            RunsGrid.SelectedItem = target;
            RunsTitle.Text = $"RUNS ({runs.Count})";
        }
        finally
        {
            _refreshing = false;
        }

        UpdateSummary(runs);
        UpdateDurationChart(runs);
        UpdateOutcomeChart(runs);

        var waitingForSavedRun = target is null && _followStartedAt is not null;
        if (!waitingForSavedRun)
            await ShowSelectionAsync(target, followed);
    }

    private void UpdateSummary(IReadOnlyList<JobRunRecord> runs)
    {
        if (runs.Count == 0)
        {
            TotalRunsValue.Text = "0";
            SuccessRateValue.Text = "-";
            AvgDurationValue.Text = "-";
            DataTransferredValue.Text = "-";
            ErrorsValue.Text = "0";
            return;
        }

        var succeeded = runs.Count(r => r.Outcome != JobRunOutcome.Failed);
        var ranRuns = runs.Where(r => r.Outcome is not (JobRunOutcome.Skipped or JobRunOutcome.SkippedAppRunning or JobRunOutcome.SkippedProjectFolderUnavailable)).ToList();
        var avgDuration = ranRuns.Count > 0
            ? TimeSpan.FromTicks((long)ranRuns.Average(r => (r.CompletedAt - r.StartedAt).Ticks))
            : (TimeSpan?)null;

        TotalRunsValue.Text = runs.Count.ToString();
        SuccessRateValue.Text = $"{succeeded * 100.0 / runs.Count:0}%";
        AvgDurationValue.Text = avgDuration is null ? "-" : RunHistoryRowViewModel.FormatDuration(avgDuration.Value);
        DataTransferredValue.Text = RunHistoryRowViewModel.FormatSize(runs.Sum(r => r.BytesCopied));
        ErrorsValue.Text = runs.Sum(r => r.ErrorCount).ToString();
    }

    private void UpdateDurationChart(IReadOnlyList<JobRunRecord> runs)
    {
        var points = runs
            .OrderBy(r => r.StartedAt)
            .Select(r =>
            {
                var duration = r.CompletedAt - r.StartedAt;
                var tooltip = $"{r.StartedAt.LocalDateTime:g}\n{RunHistoryRowViewModel.FormatDuration(duration)} – {r.Outcome}";
                return new TrendBarChartPoint(Math.Max(duration.TotalSeconds, 0), RunOutcomeDisplay.Resolve(r.Outcome), tooltip);
            })
            .ToList();

        DurationChart.SetData(points);
    }

    private static readonly JobRunOutcome[] OutcomeDisplayOrder =
    [
        JobRunOutcome.Completed, JobRunOutcome.CompletedNoChanges,
        JobRunOutcome.Skipped, JobRunOutcome.SkippedAppRunning, JobRunOutcome.SkippedProjectFolderUnavailable, JobRunOutcome.Failed,
        JobRunOutcome.DryRunCompleted, JobRunOutcome.Cancelled,
    ];

    private void UpdateOutcomeChart(IReadOnlyList<JobRunRecord> runs)
    {
        var counts = runs.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count());

        var segments = OutcomeDisplayOrder
            .Where(counts.ContainsKey)
            .Select(o => new DonutSegment(counts[o], RunOutcomeDisplay.Resolve(o), $"{RunOutcomeDisplay.FriendlyName(o)}: {counts[o]}"))
            .ToList();

        OutcomeChart.SetSegments(segments, runs.Count.ToString(), runs.Count == 1 ? "run" : "runs");

        OutcomeLegend.Children.Clear();
        foreach (var outcome in OutcomeDisplayOrder.Where(counts.ContainsKey))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = RunOutcomeDisplay.Resolve(outcome),
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{RunOutcomeDisplay.FriendlyName(outcome)} ({counts[outcome]})",
                Style = (Style)FindResource("LegendLabel"),
            });
            OutcomeLegend.Children.Add(row);
        }
    }

    private async void RunsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing)
            return;

        _followStartedAt = null;
        await ShowSelectionAsync(RunsGrid.SelectedItem as RunHistoryRowViewModel, followed: false);
    }

    private void BackToOverview_Click(object sender, RoutedEventArgs e) => RunsGrid.SelectedItem = null;

    private async Task ShowSelectionAsync(RunHistoryRowViewModel? row, bool followed)
    {
        if (row is null)
        {
            _shownRunId = null;
            _currentSteps = [];
            TopTitle.Text = "OVERVIEW";
            RunDetailPanel.Visibility = Visibility.Collapsed;
            OverviewPanel.Visibility = Visibility.Visible;
            ExecutionGraph.Clear();
            SetLog("Select a run above to view its log.");
            _filesLoadedFor = null;
            ShowFiles([], "Select a run above to see the files it changed.");
            return;
        }

        OverviewPanel.Visibility = Visibility.Collapsed;
        RunDetailPanel.Visibility = Visibility.Visible;
        TopTitle.Text = "RUN DETAILS";
        ShowRunTiles(row);

        var isNew = _shownRunId != row.RunId || _shownWasLive != row.IsRunning;
        if (!isNew)
        {
            if (row.IsRunning)
                RefreshLive();
            return;
        }

        var version = ++_selectionVersion;
        _shownRunId = row.RunId;
        _shownWasLive = row.IsRunning;
        if (!followed)
        {
            ExecutionGraph.Clear();
            _currentSteps = [];
        }

        if (row.IsRunning)
        {
            RefreshLive();
            _filesLoadedFor = null;
            ShowFiles([], "File details are saved when the run finishes.");
            return;
        }

        if (!_stepCache.TryGetValue(row.RunId, out var steps))
            steps = await _historyStore.GetRunStepsAsync(row.RunId);
        var log = await _historyStore.GetRunLogAsync(_jobName, row.RunId) ?? "(no log captured for this run)";
        if (version != _selectionVersion)
            return;

        _stepCache[row.RunId] = steps;
        ApplySteps(steps, "No step data was recorded for this run.");
        SetLog(log);

        _filesLoadedFor = null;
        if (FilesTabButton.IsChecked == true)
            await EnsureFilesLoadedAsync(row.RunId, version);
    }

    private void TopHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleSection(ref s_topCollapsed);

    private void RunsHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleSection(ref s_runsCollapsed);

    private void BottomHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleSection(ref s_bottomCollapsed);

    private void ToggleSection(ref bool collapsed)
    {
        if (!s_runsCollapsed && !s_bottomCollapsed && BottomRow.ActualHeight > 0)
            _bottomHeight = new GridLength(BottomRow.ActualHeight);

        collapsed = !collapsed;
        ApplySectionLayout();
    }

    private void ApplySectionLayout()
    {
        const string expanded = "\uE70D";
        const string collapsed = "\uE76C";

        TopContent.Visibility = s_topCollapsed ? Visibility.Collapsed : Visibility.Visible;
        RunsGrid.Visibility = s_runsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        BottomContent.Visibility = s_bottomCollapsed ? Visibility.Collapsed : Visibility.Visible;
        TopChevron.Text = s_topCollapsed ? collapsed : expanded;
        RunsChevron.Text = s_runsCollapsed ? collapsed : expanded;
        BottomChevron.Text = s_bottomCollapsed ? collapsed : expanded;
        FilesControls.Visibility = s_bottomCollapsed || FilesTabButton.IsChecked != true ? Visibility.Collapsed : Visibility.Visible;

        SectionSplitter.Visibility = s_runsCollapsed || s_bottomCollapsed ? Visibility.Collapsed : Visibility.Visible;

        if (s_runsCollapsed)
        {
            RunsRow.MinHeight = 0;
            RunsRow.Height = GridLength.Auto;
        }
        else
        {
            RunsRow.MinHeight = 120;
            RunsRow.Height = new GridLength(1, GridUnitType.Star);
        }

        if (s_bottomCollapsed)
        {
            BottomRow.MinHeight = 0;
            BottomRow.Height = GridLength.Auto;
        }
        else
        {
            BottomRow.MinHeight = 120;
            BottomRow.Height = s_runsCollapsed ? new GridLength(1, GridUnitType.Star) : _bottomHeight;
        }
    }

    private async void BottomPane_Changed(object sender, RoutedEventArgs e)
    {
        if (FilesGrid is null)
            return;

        if (s_bottomCollapsed)
        {
            s_bottomCollapsed = false;
            ApplySectionLayout();
        }

        var files = FilesTabButton.IsChecked == true;
        LogTextBox.Visibility = files ? Visibility.Collapsed : Visibility.Visible;
        FilesControls.Visibility = files && !s_bottomCollapsed ? Visibility.Visible : Visibility.Collapsed;
        UpdateFilesVisibility();

        if (files && _shownRunId is { } runId && runId != Guid.Empty && !_shownWasLive)
            await EnsureFilesLoadedAsync(runId, _selectionVersion);
    }

    private async Task EnsureFilesLoadedAsync(Guid runId, int version)
    {
        if (_filesLoadedFor == runId)
            return;

        if (!_fileCache.TryGetValue(runId, out var rows))
        {
            var files = await _historyStore.GetRunFilesAsync(runId);
            rows = files.Select(f => new RunFileRowViewModel(f)).ToList();
        }
        if (version != _selectionVersion)
            return;

        if (_fileCache.Count >= 5)
            _fileCache.Clear();
        _fileCache[runId] = rows;
        _filesLoadedFor = runId;
        var touched = _currentSteps.Any(s => s.FilesCopied + s.FilesDeleted + s.Conflicts > 0);
        ShowFiles(rows, touched
            ? "File details for this run weren't kept. The app keeps them for the most recent runs only (see Settings)."
            : "This run didn't touch any files.");
    }

    private string _filesEmptyMessage = "";

    private void ShowFiles(IReadOnlyList<RunFileRowViewModel> rows, string emptyMessage)
    {
        _fileRows = rows;
        _filesEmptyMessage = emptyMessage;
        FilesGrid.ItemsSource = rows;
        _filesView = CollectionViewSource.GetDefaultView(rows);
        _filesView.Filter = MatchesFileFilters;
        ApplyGrouping();
        UpdateFilesVisibility();
    }

    private void ApplyGrouping()
    {
        if (_filesView is null)
            return;

        using (_filesView.DeferRefresh())
        {
            _filesView.GroupDescriptions.Clear();
            _filesView.SortDescriptions.Clear();
            if (GroupByFolderBox.IsChecked == true)
            {
                _filesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RunFileRowViewModel.FolderKey)));
                _filesView.SortDescriptions.Add(new SortDescription(nameof(RunFileRowViewModel.FolderKey), ListSortDirection.Ascending));
                _filesView.SortDescriptions.Add(new SortDescription(nameof(RunFileRowViewModel.Path), ListSortDirection.Ascending));
            }
        }
    }

    private void GroupByFolder_Changed(object sender, RoutedEventArgs e)
    {
        if (FilesGrid is null)
            return;

        s_groupByFolder = GroupByFolderBox.IsChecked == true;
        ApplyGrouping();
        UpdateFilesVisibility();
    }

    private bool MatchesFileFilters(object item)
    {
        var row = (RunFileRowViewModel)item;
        if (ExecutionGraph.Selection is { } selection)
        {
            if (!string.Equals(row.DeviceName, selection.DeviceName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (selection.Phase is { } phase && row.Phase != phase)
                return false;
        }

        if (FileActionFilter.SelectedIndex > 0 && row.ActionText != (string)FileActionFilter.SelectedItem)
            return false;

        var search = FileSearchBox.Text.Trim();
        return search.Length == 0
            || row.Path.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (row.Detail?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void RefreshFileFilter()
    {
        _filesView?.Refresh();
        UpdateFilesVisibility();
    }

    private void UpdateFilesVisibility()
    {
        if (FilesGrid is null || FilesTabButton.IsChecked != true)
        {
            FilesGrid?.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
            FilesEmptyText?.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
            return;
        }

        var shown = _filesView?.Cast<object>().Count() ?? 0;
        FilesSummary.Text = _fileRows.Count == 0 ? "" : $"{shown:N0} of {_fileRows.Count:N0} files";
        FilesGrid.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
        FilesEmptyText.Visibility = shown > 0 ? Visibility.Collapsed : Visibility.Visible;
        FilesEmptyText.Text = _fileRows.Count == 0 ? _filesEmptyMessage : "No files match the current filters.";
    }

    private void FileFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FilesGrid is not null)
            RefreshFileFilter();
    }

    private void FileFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (FilesGrid is not null)
            RefreshFileFilter();
    }

    private void RefreshLive()
    {
        if (_liveSteps.TryGet(_jobName, out var steps))
            ApplySteps(steps, "");
        else if (_currentSteps.Count == 0)
            ApplySteps([], "Waiting for the run to start...");

        SetLog(_liveLog.TryGet(_jobName, out _, out var liveText)
            ? (liveText.Length == 0 ? "(waiting for log output...)" : liveText)
            : "(run just finished - refreshing...)");
    }

    private void ApplySteps(IReadOnlyList<RunStepRecord> steps, string emptyMessage)
    {
        _currentSteps = steps;
        if (steps.Count == 0)
        {
            ExecutionGraph.SetEmpty(emptyMessage);
            return;
        }

        var devices = steps.Select(s => s.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        RunGraphRow.Height = new GridLength(Math.Clamp(98 + 62 * devices, 188, 300));
        ExecutionGraph.SetSteps(steps);
    }

    private void OnLiveStepsChanged(string jobName)
    {
        if (jobName != _jobName)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded || RunsGrid.SelectedItem is not RunHistoryRowViewModel { IsRunning: true })
                return;

            if (_liveSteps.TryGet(_jobName, out var steps))
            {
                ApplySteps(steps, "");
                if (ExecutionGraph.Selection is not null)
                    ApplyLogView();
            }
        });
    }

    private void ShowRunTiles(RunHistoryRowViewModel row)
    {
        if (row.IsRunning)
        {
            RunDurationValue.Text = row.DurationText;
            RunFilesValue.Text = "-";
            RunSizeValue.Text = "-";
            RunErrorsValue.Text = "-";
            RunOutcomeValue.Text = row.OutcomeText;
            RunOutcomeValue.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Accent");
            return;
        }

        RunDurationValue.Text = row.DurationText;
        RunFilesValue.Text = $"{row.FilesCopied} copied · {row.FilesDeleted} deleted";
        RunSizeValue.Text = row.SizeText;
        RunErrorsValue.Text = row.ErrorCount.ToString();
        RunOutcomeValue.Text = row.OutcomeText;
        RunOutcomeValue.Foreground = RunOutcomeDisplay.Resolve(row.Outcome);
    }

    private void ExecutionGraph_SelectionChanged(RunGraphSelection? selection)
    {
        GraphHint.Text = selection is null
            ? DefaultGraphHint
            : $"Filtered to {selection.DeviceName}{(selection.Phase is { } phase ? $" · {phase}" : "")} - click again to clear";
        ApplyLogView();
        RefreshFileFilter();
    }

    private void SetLog(string text)
    {
        _fullLog = text;
        ApplyLogView();
    }

    private void ApplyLogView()
    {
        var text = BuildLogView();
        if (LogTextBox.Text == text)
            return;

        var atEnd = LogTextBox.VerticalOffset + LogTextBox.ViewportHeight >= LogTextBox.ExtentHeight - 1;
        var offset = LogTextBox.VerticalOffset;
        LogTextBox.Text = text;
        if (_shownWasLive && atEnd)
            LogTextBox.ScrollToEnd();
        else
            LogTextBox.ScrollToVerticalOffset(offset);
    }

    private string BuildLogView()
    {
        if (ExecutionGraph.Selection is not { } selection || RunDetailPanel.Visibility != Visibility.Visible)
            return _fullLog;

        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        if (selection.Phase is { } phase)
        {
            var step = _currentSteps.FirstOrDefault(s =>
                s.Phase == phase && string.Equals(s.DeviceName, selection.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (step?.StartedAt is not { } started)
                return $"({phase} on {selection.DeviceName} didn't run - nothing to show)";
            from = started;
            to = step.EndedAt;
        }

        var filtered = RunLogFilter.Filter(_fullLog, selection.DeviceName, from, to);
        return filtered.Length == 0
            ? $"(no log lines matched {selection.DeviceName}{(selection.Phase is { } p ? $" · {p}" : "")})"
            : filtered;
    }
}
