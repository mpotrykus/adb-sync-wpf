using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AdbSync.App.Services;
using AdbSync.App.ViewModels;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Models.Devices;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using AdbSync.Core.Services.Orchestration.RunHistory;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Models.Transfer;
using AdbSync.Core.Services.Transfer;

namespace AdbSync.App.Views;

public partial class DashboardWindow : Window
{
    private readonly AppConfigService _configService;
    private readonly DashboardUiStateStore _uiStateStore;
    private readonly JobRunService _jobRunService;
    private readonly DashboardViewModel _viewModel;
    private readonly IRunHistoryStore _historyStore;
    private readonly ILiveRunLogSink _liveLogSink;
    private readonly IAdbDeviceResolver _deviceResolver;
    private readonly IDeviceChangeWatcher _changeWatcher;
    private readonly IRemoteFileSystemFactory _remoteFileSystemFactory;
    private readonly IDevicePackageLister _packageLister;
    private readonly DispatcherTimer _relativeTimeTimer;

    // Keyed by job name; the "Add Job" editor uses this sentinel since it has no job name yet.
    private const string NewJobKey = "\0new-job";
    private readonly Dictionary<string, JobEditorWindow> _openJobEditors = new();
    private readonly Dictionary<string, RunHistoryWindow> _openRunHistoryWindows = new();
    private DeviceEditorWindow? _deviceEditorWindow;
    private SettingsWindow? _settingsWindow;

    // The user's last chosen sort column/direction, kept for the life of the window (it's hidden, not recreated,
    // between tray show/hide cycles) so it survives every SyncFrom-driven refresh rather than resetting to default.
    private string _sortMemberPath = nameof(JobStatusViewModel.Name);
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    public DashboardWindow(
        AppConfigService configService, DashboardUiStateStore uiStateStore, JobRunService jobRunService, DashboardViewModel viewModel,
        IRunHistoryStore historyStore, ILiveRunLogSink liveLogSink, IAdbDeviceResolver deviceResolver, IDeviceChangeWatcher changeWatcher,
        IRemoteFileSystemFactory remoteFileSystemFactory, IDevicePackageLister packageLister)
    {
        InitializeComponent();
        _configService = configService;
        _uiStateStore = uiStateStore;
        _jobRunService = jobRunService;
        _viewModel = viewModel;
        _historyStore = historyStore;
        _liveLogSink = liveLogSink;
        _deviceResolver = deviceResolver;
        _changeWatcher = changeWatcher;
        _remoteFileSystemFactory = remoteFileSystemFactory;
        _packageLister = packageLister;
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

        ApplySort();

        Loaded += async (_, _) =>
        {
            await LoadPersistedSortAsync();
            await RefreshAsync();
        };
        _configService.ConfigChanged += async (_, _) => await Dispatcher.InvokeAsync(RefreshAsync);
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.F12) Application.Current.Shutdown(); };
    }

    private async Task LoadPersistedSortAsync()
    {
        var state = await _uiStateStore.LoadAsync();
        if (state.SortColumn is not { Length: > 0 } sortColumn || JobsGrid.Columns.All(c => c.SortMemberPath != sortColumn))
            return;

        _sortMemberPath = sortColumn;
        _sortDirection = state.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        ApplySort();
    }

    private async Task RefreshAsync()
    {
        var config = await _configService.GetAsync();
        _viewModel.SyncFrom(config);
        await PopulateLastOutcomesAsync(config);

        // SyncFrom adds/removes rows in the same ObservableCollection, so its default CollectionView normally
        // keeps sorting itself automatically - this reapplies explicitly so a refresh can never drop the user's
        // chosen sort column/direction or leave a stale arrow on the header.
        ApplySort();
    }

    private void JobsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (e.Column.SortMemberPath is not { Length: > 0 } sortMemberPath)
            return;

        e.Handled = true;
        _sortDirection = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        _sortMemberPath = sortMemberPath;
        ApplySort();

        _ = _uiStateStore.SaveAsync(new DashboardUiState
        {
            SortColumn = _sortMemberPath,
            SortDescending = _sortDirection == ListSortDirection.Descending,
        });
    }

    private void ApplySort()
    {
        var view = CollectionViewSource.GetDefaultView(_viewModel.Jobs);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(_sortMemberPath, _sortDirection));

        foreach (var column in JobsGrid.Columns)
            column.SortDirection = column.SortMemberPath == _sortMemberPath ? _sortDirection : null;

        // Driven from code rather than a XAML Trigger.EnterActions storyboard: BeginAnimation always cleanly
        // replaces whatever's currently animating the property, so repeated toggles (unlike trigger-driven
        // storyboards, which only reliably fire the first time a given trigger becomes active) always animate.
        foreach (var header in FindVisualChildren<DataGridColumnHeader>(JobsGrid))
        {
            if (header.Column is not { } column || header.Template?.FindName("SortArrowRotate", header) is not RotateTransform rotate)
                continue;

            var targetAngle = column.SortDirection switch
            {
                ListSortDirection.Ascending => 0.0,
                ListSortDirection.Descending => 180.0,
                _ => (double?)null,
            };
            if (targetAngle is { } angle)
                rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, TimeSpan.FromMilliseconds(150)));
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
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
        JobRunOutcome.DryRunCompleted => "Dry run completed",
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
        if (_openJobEditors.TryGetValue(NewJobKey, out var existing))
        {
            existing.Activate();
            return;
        }

        var config = await _configService.GetAsync();
        var editor = new JobEditorWindow(config, job: null, _deviceResolver, _changeWatcher, _remoteFileSystemFactory, _packageLister) { Owner = this };
        _openJobEditors[NewJobKey] = editor;
        editor.Closed += async (_, _) =>
        {
            _openJobEditors.Remove(NewJobKey);
            if (editor.Result is null)
                return;
            config.Jobs.Add(editor.Result);
            await _configService.SaveAsync();
            await RefreshAsync();
        };
        editor.Show();
        editor.Activate();
    }

    private async void EditJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        if (_openJobEditors.TryGetValue(vm.Name, out var existing))
        {
            existing.Activate();
            return;
        }

        var config = await _configService.GetAsync();
        var job = config.Jobs.FirstOrDefault(j => j.Name == vm.Name);
        if (job is null)
            return;

        var editor = new JobEditorWindow(config, job, _deviceResolver, _changeWatcher, _remoteFileSystemFactory, _packageLister) { Owner = this };
        _openJobEditors[vm.Name] = editor;
        editor.Closed += async (_, _) =>
        {
            _openJobEditors.Remove(vm.Name);
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
        editor.Activate();
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

    private async void Checkpoint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        var config = await _configService.GetAsync();
        var index = config.Jobs.FindIndex(j => j.Name == vm.Name);
        if (index < 0)
            return;

        var choice = ConfirmDialog.ShowWithResult(this, "Checkpoint",
            $"This pulls the current contents of every device in '{vm.Name}' into a new, timestamped backup folder inside its project directory.\n\n" +
            "It does not touch the sync master, staging, or the devices themselves, so it won't be modified or removed by future sync runs.\n\n" +
            "Use it to snapshot device state before running a job you're unsure about.",
            confirmText: "Checkpoint", secondaryText: "Restore Checkpoint...");

        switch (choice)
        {
            case ConfirmDialogResult.Confirm:
                await CreateCheckpointAsync(vm, index);
                break;
            case ConfirmDialogResult.Secondary:
                await RestoreCheckpointAsync(vm, index);
                break;
        }
    }

    private async Task CreateCheckpointAsync(JobStatusViewModel vm, int index)
    {
        // No SyncJobRunner/ISyncEventSink involved here, so this handler drives the row's phase/outcome text itself.
        vm.PhaseText = "Checkpoint";
        try
        {
            var result = await _jobRunService.CreateSnapshotAsync(index);
            vm.ReportOutcome(result.Errors == 0
                ? $"Checkpoint saved ({result.TotalFiles} file(s))"
                : $"Checkpoint saved with {result.Errors} error(s)");
        }
        catch (Exception ex)
        {
            vm.NeedsAttention = true;
            vm.ReportOutcome($"Error: {ex.Message}");
        }
        finally
        {
            vm.PhaseText = "Idle";
        }
    }

    private async Task RestoreCheckpointAsync(JobStatusViewModel vm, int index)
    {
        var snapshots = await _jobRunService.ListSnapshotsAsync(index);
        if (snapshots.Count == 0)
        {
            MessageBox.Show(this, $"No checkpoints found for '{vm.Name}'.", "Restore Checkpoint", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new RestoreCheckpointWindow(vm.Name, snapshots) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedSnapshot is not { } snapshot)
            return;

        var confirmed = ConfirmDialog.Show(this, "Restore Checkpoint",
            $"This pushes the checkpoint from {snapshot.CreatedAt.LocalDateTime:g} back out to every matching device in '{vm.Name}', " +
            "overwriting current device files and deleting any device file that isn't in the checkpoint. This cannot be undone.",
            confirmText: "Restore");
        if (!confirmed)
            return;

        vm.PhaseText = "Restoring";
        try
        {
            var result = await _jobRunService.RestoreSnapshotAsync(index, snapshot.Path);
            var skippedNote = result.SkippedDevices is { Count: > 0 } skipped ? $", skipped {skipped.Count} unmatched device(s)" : "";
            vm.ReportOutcome(result.Errors == 0
                ? $"Checkpoint restored ({result.TotalFiles} file(s)){skippedNote}"
                : $"Checkpoint restored with {result.Errors} error(s){skippedNote}");
        }
        catch (Exception ex)
        {
            vm.NeedsAttention = true;
            vm.ReportOutcome($"Error: {ex.Message}");
        }
        finally
        {
            vm.PhaseText = "Idle";
        }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        var config = await _configService.GetAsync();
        var job = config.Jobs.FirstOrDefault(j => j.Name == vm.Name);
        if (job is null)
            return;

        var eff = job.Resolve(config.Settings);
        if (string.IsNullOrWhiteSpace(eff.ProjectsDirectory))
            return;

        var projectRoot = Path.Combine(eff.ProjectsDirectory, job.Name);
        Directory.CreateDirectory(projectRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{projectRoot}\"") { UseShellExecute = true });
    }

    private void ViewLogs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        if (_openRunHistoryWindows.TryGetValue(vm.Name, out var existing))
        {
            existing.Activate();
            return;
        }

        var window = new RunHistoryWindow(_historyStore, vm.Name, _viewModel, _liveLogSink) { Owner = this };
        _openRunHistoryWindows[vm.Name] = window;
        window.Closed += (_, _) => _openRunHistoryWindows.Remove(vm.Name);
        window.Show();
        window.Activate();
    }

    private async void ManageDevices_Click(object sender, RoutedEventArgs e)
    {
        if (_deviceEditorWindow is not null)
        {
            _deviceEditorWindow.Activate();
            return;
        }

        var config = await _configService.GetAsync();
        var window = new DeviceEditorWindow(config) { Owner = this };
        _deviceEditorWindow = window;
        window.Closed += async (_, _) =>
        {
            _deviceEditorWindow = null;
            if (window.Changed)
                await _configService.SaveAsync();
        };
        window.Show();
        window.Activate();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e) => await OpenSettingsAsync();

    // Public so other entry points (e.g. the tray icon menu) reuse this window instead of spawning their own.
    public async Task OpenSettingsAsync()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var config = await _configService.GetAsync();
        var window = new SettingsWindow(config) { Owner = this };
        _settingsWindow = window;
        window.Closed += async (_, _) =>
        {
            _settingsWindow = null;
            if (window.Saved)
            {
                await _configService.SaveAsync();
                await RefreshAsync();
            }
        };
        window.Show();
        window.Activate();
    }
}
