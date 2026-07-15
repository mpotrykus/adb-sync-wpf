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
    private readonly ChangeWatchHostedService _changeWatchHostedService;
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
        AppConfigService configService, DashboardUiStateStore uiStateStore, JobRunService jobRunService, ChangeWatchHostedService changeWatchHostedService, DashboardViewModel viewModel,
        IRunHistoryStore historyStore, ILiveRunLogSink liveLogSink, IAdbDeviceResolver deviceResolver, IDeviceChangeWatcher changeWatcher,
        IRemoteFileSystemFactory remoteFileSystemFactory, IDevicePackageLister packageLister)
    {
        InitializeComponent();
        _configService = configService;
        _uiStateStore = uiStateStore;
        _jobRunService = jobRunService;
        _changeWatchHostedService = changeWatchHostedService;
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
        await PopulateCheckpointsAsync(config);

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

    // Populates the checkpoint badge from whatever's on disk (AdbSync.Core.Services.Orchestration.CheckpointManager),
    // not from run outcomes - a checkpoint can be left behind by a hard crash that never got the chance to report
    // any outcome at all, which is exactly the case this badge exists to surface.
    private async Task PopulateCheckpointsAsync(AppConfig config)
    {
        var checkpoints = await _jobRunService.GetAllCheckpointsAsync();
        var byJobName = checkpoints.ToDictionary(c => c.ProjectName ?? "", StringComparer.Ordinal);

        foreach (var vm in _viewModel.Jobs)
        {
            if (byJobName.TryGetValue(vm.Name, out var checkpoint))
                ApplyCheckpoint(vm, checkpoint, config);
            else
                ClearCheckpoint(vm);
        }
    }

    private static void ApplyCheckpoint(JobStatusViewModel vm, SyncCheckpoint checkpoint, AppConfig config)
    {
        var job = config.Jobs.FirstOrDefault(j => j.Name == vm.Name);
        var deviceName = job is not null && checkpoint.DeviceIndex >= 0 && checkpoint.DeviceIndex < job.Devices.Count
            ? job.Devices[checkpoint.DeviceIndex].DeviceName
            : null;

        var where = deviceName is not null ? $"{checkpoint.Phase} @ {deviceName}" : checkpoint.Phase.ToString();
        vm.HasCheckpoint = true;
        vm.CheckpointSummary = $"Interrupted during {where} - saved {JobStatusViewModel.FormatRelative(checkpoint.SavedAt)}";
    }

    private static void ClearCheckpoint(JobStatusViewModel vm)
    {
        vm.HasCheckpoint = false;
        vm.CheckpointSummary = null;
    }

    private static string FormatOutcome(JobRunRecord record) => record.Outcome switch
    {
        JobRunOutcome.Completed => "Success",
        JobRunOutcome.CompletedNoChanges => "No changes",
        JobRunOutcome.Skipped => "Skipped: already running",
        JobRunOutcome.SkippedAppRunning => "Skipped: app running",
        JobRunOutcome.Failed => $"Error: {record.ErrorMessage}",
        JobRunOutcome.DryRunCompleted => "Dry run completed",
        JobRunOutcome.Cancelled => "Stopped",
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
        await _jobRunService.DiscardCheckpointAsync(vm.Name); // best-effort - don't leave an orphan checkpoint file for a job that no longer exists
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
        if (index < 0)
            return;

        await _jobRunService.RunJobAsync(index); // live status flows back via ISyncEventSink while this runs

        // Re-check against disk rather than assume from the outcome: a run that fails or gets stopped mid-push
        // may have just written a *new* checkpoint, while a clean completion clears the old one.
        var checkpoint = await _jobRunService.GetCheckpointAsync(vm.Name);
        if (checkpoint is not null)
            ApplyCheckpoint(vm, checkpoint, await _configService.GetAsync());
        else
            ClearCheckpoint(vm);
    }

    private async void StopNow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;
        if (vm.IsStopping)
            return;

        // Only push can leave devices in a mismatched state if interrupted partway - pull/merge/pre-connect
        // stop without ceremony since nothing device-visible is left half-done.
        if (vm.PhaseText.StartsWith("Push", StringComparison.Ordinal))
        {
            var confirmed = ConfirmDialog.Show(this, "Stop Job",
                $"'{vm.Name}' is currently pushing to a device. Stopping now may leave some devices updated and others not, " +
                "an incomplete state until the job is run again.",
                confirmText: "Stop Job");
            if (!confirmed)
                return;
        }

        var config = await _configService.GetAsync();
        var index = config.Jobs.FindIndex(j => j.Name == vm.Name);
        // A change-watch job waiting for its app to close (or queued behind a concurrency gate) hasn't handed
        // off to JobRunService yet, so CancelJob(index) has nothing to cancel there - fall back to interrupting
        // the watch's own wait/trigger cycle instead.
        var cancelled = index >= 0 && _jobRunService.CancelJob(index);
        cancelled |= _changeWatchHostedService.CancelJob(vm.Name);
        if (cancelled)
            vm.IsStopping = true;
    }

    private async void DiscardCheckpoint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        var confirmed = ConfirmDialog.Show(this, "Discard Checkpoint",
            $"Job '{vm.Name}' was interrupted ({vm.CheckpointSummary}). Discarding its checkpoint means the " +
            "next run starts over from the very beginning (a full re-pull and re-merge) instead of continuing " +
            "where it left off. Files already synced are not affected.",
            confirmText: "Discard");
        if (!confirmed)
            return;

        await _jobRunService.DiscardCheckpointAsync(vm.Name);
        ClearCheckpoint(vm);
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

    private async void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        var config = await _configService.GetAsync();
        var index = config.Jobs.FindIndex(j => j.Name == vm.Name);
        if (index < 0)
            return;

        var choice = ConfirmDialog.ShowWithResult(this, "Snapshot",
            $"This pulls the current contents of every device in '{vm.Name}' into a new, timestamped backup folder inside its project directory.\n\n" +
            "It does not touch the sync master, staging, or the devices themselves, so it won't be modified or removed by future sync runs.\n\n" +
            "Use it to snapshot device state before running a job you're unsure about.",
            confirmText: "Snapshot", secondaryText: "Restore Snapshot...");

        switch (choice)
        {
            case ConfirmDialogResult.Confirm:
                await CreateSnapshotWorkflowAsync(vm, index);
                break;
            case ConfirmDialogResult.Secondary:
                await RestoreSnapshotWorkflowAsync(vm, index);
                break;
        }
    }

    private async Task CreateSnapshotWorkflowAsync(JobStatusViewModel vm, int index)
    {
        // No SyncJobRunner/ISyncEventSink involved here, so this handler drives the row's phase/outcome text itself.
        vm.PhaseText = "Snapshot";
        try
        {
            var result = await _jobRunService.CreateSnapshotAsync(index);
            vm.ReportOutcome(result.Errors == 0
                ? $"Snapshot saved ({result.TotalFiles} file(s))"
                : $"Snapshot saved with {result.Errors} error(s)");
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

    private async Task RestoreSnapshotWorkflowAsync(JobStatusViewModel vm, int index)
    {
        var snapshots = await _jobRunService.ListSnapshotsAsync(index);
        if (snapshots.Count == 0)
        {
            MessageBox.Show(this, $"No snapshots found for '{vm.Name}'.", "Restore Snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new RestoreSnapshotWindow(vm.Name, snapshots) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedSnapshot is not { } snapshot)
            return;

        var confirmed = ConfirmDialog.Show(this, "Restore Snapshot",
            $"This pushes the snapshot from {snapshot.CreatedAt.LocalDateTime:g} back out to every matching device in '{vm.Name}', " +
            "overwriting current device files and deleting any device file that isn't in the snapshot. This cannot be undone.",
            confirmText: "Restore");
        if (!confirmed)
            return;

        vm.PhaseText = "Restoring";
        try
        {
            var result = await _jobRunService.RestoreSnapshotAsync(index, snapshot.Path);
            var skippedNote = result.SkippedDevices is { Count: > 0 } skipped ? $", skipped {skipped.Count} unmatched device(s)" : "";
            vm.ReportOutcome(result.Errors == 0
                ? $"Snapshot restored ({result.TotalFiles} file(s)){skippedNote}"
                : $"Snapshot restored with {result.Errors} error(s){skippedNote}");
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
