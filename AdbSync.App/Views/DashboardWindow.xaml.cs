using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AdbSync.App.Services;
using AdbSync.App.ViewModels;
using AdbSync.Core.Config;
using AdbSync.Core.Devices;
using AdbSync.Core.Orchestration;
using AdbSync.Core.Orchestration.RunHistory;
using AdbSync.Core.Transfer;

namespace AdbSync.App.Views;

public partial class DashboardWindow : Window
{
    private readonly AppConfigService _configService;
    private readonly JobRunService _jobRunService;
    private readonly DashboardViewModel _viewModel;
    private readonly IRunHistoryStore _historyStore;
    private readonly IAdbDeviceResolver _deviceResolver;
    private readonly IDeviceChangeWatcher _changeWatcher;
    private readonly IRemoteFileSystemFactory _remoteFileSystemFactory;
    private readonly DispatcherTimer _relativeTimeTimer;

    public DashboardWindow(
        AppConfigService configService, JobRunService jobRunService, DashboardViewModel viewModel, IRunHistoryStore historyStore,
        IAdbDeviceResolver deviceResolver, IDeviceChangeWatcher changeWatcher, IRemoteFileSystemFactory remoteFileSystemFactory)
    {
        InitializeComponent();
        _configService = configService;
        _jobRunService = jobRunService;
        _viewModel = viewModel;
        _historyStore = historyStore;
        _deviceResolver = deviceResolver;
        _changeWatcher = changeWatcher;
        _remoteFileSystemFactory = remoteFileSystemFactory;
        DataContext = viewModel;

        // Only ticks while the window is visible - it's tray-resident and hidden (not closed) most of the time,
        // so IsVisibleChanged (not Loaded/Closed) is what actually tracks when it's worth updating.
        _relativeTimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _relativeTimeTimer.Tick += (_, _) =>
        {
            foreach (var job in _viewModel.Jobs)
                job.RefreshLastRunText();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                _relativeTimeTimer.Start();
            else
                _relativeTimeTimer.Stop();
        };

        Loaded += async (_, _) => await RefreshAsync();
        _configService.ConfigChanged += async (_, _) => await Dispatcher.InvokeAsync(RefreshAsync);
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.F12) Application.Current.Shutdown(); };
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
            if (runs.Count == 0)
                continue;

            var lastRun = runs[0];
            vm.LastOutcome = FormatOutcome(lastRun);
            if (lastRun.Outcome == JobRunOutcome.Failed)
            {
                vm.NeedsAttention = true;
                vm.CanForcePush = lastRun.ErrorMessage?.Contains("Safety check blocked push", StringComparison.Ordinal) == true;
            }
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
        var editor = new JobEditorWindow(config, job: null, _deviceResolver, _changeWatcher, _remoteFileSystemFactory) { Owner = this };
        editor.Closed += async (_, _) =>
        {
            if (editor.Result is null)
                return;
            config.Jobs.Add(editor.Result);
            await _configService.SaveAsync();
            await RefreshAsync();
        };
        editor.Show();
    }

    private async void EditJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        var config = await _configService.GetAsync();
        var job = config.Jobs.FirstOrDefault(j => j.Name == vm.Name);
        if (job is null)
            return;

        var editor = new JobEditorWindow(config, job, _deviceResolver, _changeWatcher, _remoteFileSystemFactory) { Owner = this };
        editor.Closed += async (_, _) =>
        {
            if (editor.Result is null)
                return;
            var index = config.Jobs.IndexOf(job);
            if (index < 0)
                return; // job was removed from another window while this editor was open
            config.Jobs[index] = editor.Result;
            await _configService.SaveAsync();
            await RefreshAsync();
        };
        editor.Show();
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
        // Read the checkbox's own IsChecked rather than the bound JobStatusViewModel.Enabled: inside this
        // DataGrid template column, the TwoWay binding's target-to-source push lags behind the Click event
        // (the click handler otherwise observes the pre-toggle value), so trusting the viewmodel here silently
        // re-saves the old state. JobEditorWindow's EnabledCheckBox doesn't hit this because it reads straight
        // off the control too, never through a bound viewmodel property.
        if (sender is not CheckBox { DataContext: JobStatusViewModel vm } checkBox)
            return;

        var isEnabled = checkBox.IsChecked == true;

        var config = await _configService.GetAsync();
        var job = config.Jobs.FirstOrDefault(j => j.Name == vm.Name);
        if (job is null)
            return;

        job.Enabled = isEnabled;
        vm.Enabled = isEnabled;
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

    private async void ForcePush_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        var confirmed = ConfirmDialog.Show(this, "Force Push",
            $"This bypasses the push-safety check for '{vm.Name}' and pushes its current local contents to all devices now, " +
            "deleting any device files that aren't present locally. The new file count also becomes the accepted baseline going forward.",
            confirmText: "Force Push");
        if (!confirmed)
            return;

        var config = await _configService.GetAsync();
        var index = config.Jobs.FindIndex(j => j.Name == vm.Name);
        if (index >= 0)
            _ = _jobRunService.RunJobAsync(index, forcePush: true); // fire-and-forget; live status flows back via ISyncEventSink
    }

    private void ViewLogs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        new RunHistoryWindow(_historyStore, vm.Name) { Owner = this }.Show();
    }

    private async void ManageDevices_Click(object sender, RoutedEventArgs e)
    {
        var config = await _configService.GetAsync();
        var window = new DeviceEditorWindow(config) { Owner = this };
        window.Closed += async (_, _) =>
        {
            if (window.Changed)
                await _configService.SaveAsync();
        };
        window.Show();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var config = await _configService.GetAsync();
        var window = new SettingsWindow(config) { Owner = this };
        window.Closed += async (_, _) =>
        {
            if (window.Saved)
            {
                await _configService.SaveAsync();
                await RefreshAsync();
            }
        };
        window.Show();
    }
}
