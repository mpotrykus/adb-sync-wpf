using AdbSync.App.Controls;
using AdbSync.App.Converters;
using AdbSync.App.ViewModels;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Orchestration.RunHistory;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AdbSync.App.Views;

public partial class RunHistoryWindow : Window
{
    private static readonly TimeSpan IdleRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromSeconds(1);

    private readonly IRunHistoryStore _historyStore;
    private readonly string _jobName;
    private readonly DashboardViewModel _dashboard;
    private readonly ILiveRunLogSink _liveLog;
    private readonly DispatcherTimer _autoRefreshTimer;

    public RunHistoryWindow(IRunHistoryStore historyStore, string jobName, DashboardViewModel dashboard, ILiveRunLogSink liveLog)
    {
        InitializeComponent();
        _historyStore = historyStore;
        _jobName = jobName;
        _dashboard = dashboard;
        _liveLog = liveLog;
        Title = $"Run History - {jobName}";

        _autoRefreshTimer = new DispatcherTimer { Interval = IdleRefreshInterval };
        _autoRefreshTimer.Tick += async (_, _) => await RefreshAsync();

        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            _autoRefreshTimer.Start();
        };
        Closed += (_, _) => _autoRefreshTimer.Stop();
    }

    private bool IsJobRunning() => _dashboard.Jobs.FirstOrDefault(j => j.Name == _jobName)?.IsRunning ?? false;

    private async Task RefreshAsync()
    {
        var selectedRunId = (RunsGrid.SelectedItem as RunHistoryRowViewModel)?.RunId;

        var runs = await _historyStore.ListRunsAsync(_jobName);
        var rows = runs.Select(r => new RunHistoryRowViewModel(r)).ToList();

        var runningJob = _dashboard.Jobs.FirstOrDefault(j => j.Name == _jobName);
        var isRunning = runningJob?.IsRunning ?? false;
        if (isRunning && _liveLog.TryGet(_jobName, out var startedAt, out _))
        {
            rows.Insert(0, new RunHistoryRowViewModel(runningJob!.PhaseText, startedAt));
        }
        _autoRefreshTimer.Interval = isRunning ? LiveRefreshInterval : IdleRefreshInterval;

        RunsGrid.ItemsSource = rows;

        if (selectedRunId is not null)
        {
            RunsGrid.SelectedItem = rows.FirstOrDefault(r => r.RunId == selectedRunId);
        }

        UpdateSummary(runs);
        UpdateDurationChart(runs);
        UpdateOutcomeChart(runs);
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
        var ranRuns = runs.Where(r => r.Outcome is not (JobRunOutcome.Skipped or JobRunOutcome.SkippedAppRunning)).ToList();
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
        JobRunOutcome.Skipped, JobRunOutcome.SkippedAppRunning, JobRunOutcome.Failed,
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
        if (RunsGrid.SelectedItem is not RunHistoryRowViewModel row)
        {
            LogTextBox.Text = "Select a run above to view its log.";
            return;
        }

        if (row.IsRunning)
        {
            LogTextBox.Text = _liveLog.TryGet(_jobName, out _, out var liveText)
                ? (liveText.Length == 0 ? "(waiting for log output...)" : liveText)
                : "(run just finished - refreshing...)";
            return;
        }

        LogTextBox.Text = await _historyStore.GetRunLogAsync(_jobName, row.RunId) ?? "(no log captured for this run)";
    }
}
