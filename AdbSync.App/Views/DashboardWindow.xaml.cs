using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdbSync.App.Services;
using AdbSync.App.ViewModels;
using AdbSync.Core.Config;
using AdbSync.Core.Orchestration;
using AdbSync.Core.Orchestration.RunHistory;

namespace AdbSync.App.Views;

public partial class DashboardWindow : Window
{
    private readonly AppConfigService _configService;
    private readonly JobRunService _jobRunService;
    private readonly DashboardViewModel _viewModel;
    private readonly IRunHistoryStore _historyStore;

    public DashboardWindow(AppConfigService configService, JobRunService jobRunService, DashboardViewModel viewModel, IRunHistoryStore historyStore)
    {
        InitializeComponent();
        _configService = configService;
        _jobRunService = jobRunService;
        _viewModel = viewModel;
        _historyStore = historyStore;
        DataContext = viewModel;

        Loaded += async (_, _) => await RefreshAsync();
        _configService.ConfigChanged += async (_, _) => await Dispatcher.InvokeAsync(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        var config = await _configService.GetAsync();
        _viewModel.SyncFrom(config);
        await PopulateLastOutcomesAsync(config);
    }

    // SyncFrom only knows the last-run timestamp from job config; the human-readable outcome text lives in run
    // history and is otherwise only populated by live JobCompleted/JobFailed/JobSkipped events, so on a fresh
    // app start the dashboard would show a blank result until the first run since launch.
    private async Task PopulateLastOutcomesAsync(AppConfig config)
    {
        foreach (var job in config.Jobs)
        {
            var vm = _viewModel.Jobs.FirstOrDefault(j => j.Name == job.Name);
            if (vm is null || vm.LastOutcome is not null)
                continue;

            var runs = await _historyStore.ListRunsAsync(job.Name);
            if (runs.Count > 0)
                vm.LastOutcome = FormatOutcome(runs[0]);
        }
    }

    private static string FormatOutcome(JobRunRecord record) => record.Outcome switch
    {
        JobRunOutcome.Completed => "Success",
        JobRunOutcome.CompletedNoChanges => "No changes",
        JobRunOutcome.Skipped => "Skipped: already running",
        JobRunOutcome.SkippedAppRunning => "Skipped: app running",
        JobRunOutcome.Failed => $"Error: {record.ErrorMessage}",
        _ => record.Outcome.ToString(),
    };

    private void JobsGridBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var border = (Border)sender;
        var radius = border.CornerRadius.TopLeft;
        border.Clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Tray-resident app: hide rather than destroy, so re-opening from the tray reuses this window.
        e.Cancel = true;
        Hide();
    }

    private async void AddJob_Click(object sender, RoutedEventArgs e)
    {
        var config = await _configService.GetAsync();
        var editor = new JobEditorWindow(config, job: null) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            config.Jobs.Add(editor.Result!);
            await _configService.SaveAsync();
            await RefreshAsync();
        }
    }

    private async void EditJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        var config = await _configService.GetAsync();
        var job = config.Jobs.FirstOrDefault(j => j.Name == vm.Name);
        if (job is null)
            return;

        var editor = new JobEditorWindow(config, job) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            var index = config.Jobs.IndexOf(job);
            config.Jobs[index] = editor.Result!;
            await _configService.SaveAsync();
            await RefreshAsync();
        }
    }

    private async void RemoveJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        var confirmed = MessageBox.Show(this, $"Remove job '{vm.Name}'?", "Remove Job", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmed != MessageBoxResult.Yes)
            return;

        var config = await _configService.GetAsync();
        config.Jobs.RemoveAll(j => j.Name == vm.Name);
        await _configService.SaveAsync();
        await RefreshAsync();
    }

    private async void JobEnabledToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        var config = await _configService.GetAsync();
        var job = config.Jobs.FirstOrDefault(j => j.Name == vm.Name);
        if (job is null)
            return;

        job.Enabled = vm.Enabled;
        await _configService.SaveAsync();
        await RefreshAsync();
    }

    private async void RunNow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        var config = await _configService.GetAsync();
        var index = config.Jobs.FindIndex(j => j.Name == vm.Name);
        if (index >= 0)
            _ = _jobRunService.RunJobAsync(index); // fire-and-forget; live status flows back via ISyncEventSink
    }

    private void ViewLogs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        new RunHistoryWindow(_historyStore, vm.Name) { Owner = this }.ShowDialog();
    }

    private async void ManageDevices_Click(object sender, RoutedEventArgs e)
    {
        var config = await _configService.GetAsync();
        var window = new DeviceEditorWindow(config) { Owner = this };
        if (window.ShowDialog() == true)
            await _configService.SaveAsync();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var config = await _configService.GetAsync();
        var window = new SettingsWindow(config) { Owner = this };
        if (window.ShowDialog() == true)
        {
            await _configService.SaveAsync();
            await RefreshAsync();
        }
    }
}
