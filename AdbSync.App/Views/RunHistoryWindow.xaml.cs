using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using AdbSync.App.Controls;
using AdbSync.App.Converters;
using AdbSync.App.ViewModels;
using AdbSync.Core.Orchestration;
using AdbSync.Core.Orchestration.RunHistory;

namespace AdbSync.App.Views;

public partial class RunHistoryWindow : Window
{
    private readonly IRunHistoryStore _historyStore;
    private readonly string _jobName;

    public RunHistoryWindow(IRunHistoryStore historyStore, string jobName)
    {
        InitializeComponent();
        _historyStore = historyStore;
        _jobName = jobName;
        Title = $"Run History - {jobName}";

        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var runs = await _historyStore.ListRunsAsync(_jobName);
        RunsGrid.ItemsSource = runs.Select(r => new RunHistoryRowViewModel(r)).ToList();

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
        var avgDuration = TimeSpan.FromTicks((long)runs.Average(r => (r.CompletedAt - r.StartedAt).Ticks));

        TotalRunsValue.Text = runs.Count.ToString();
        SuccessRateValue.Text = $"{succeeded * 100.0 / runs.Count:0}%";
        AvgDurationValue.Text = RunHistoryRowViewModel.FormatDuration(avgDuration);
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
                Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0),
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

        LogTextBox.Text = await _historyStore.GetRunLogAsync(_jobName, row.RunId) ?? "(no log captured for this run)";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
