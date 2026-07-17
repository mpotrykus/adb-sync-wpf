using AdbSync.App.Services;
using AdbSync.App.ViewModels;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Orchestration.RunHistory;
using AdbSync.Core.Services.Transfer;
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

    private const string NewJobKey = "\0new-job";
    private readonly Dictionary<string, JobEditorWindow> _openJobEditors = new();
    private readonly Dictionary<string, RunHistoryWindow> _openRunHistoryWindows = new();
    private DeviceEditorWindow? _deviceEditorWindow;
    private SettingsWindow? _settingsWindow;

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
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.F12)
                return;
            if (_viewModel.AnyJobRunning && !ConfirmDialog.Show(this, "Exit AdbSync", _viewModel.ExitWarningMessage, confirmText: "Exit"))
                return;
            Application.Current.Shutdown();
        };
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
        var totalDevices = job?.Devices.Count ?? 0;

        var where = totalDevices > 0
            ? $"{checkpoint.Phase} ({checkpoint.CompletedDevices.Count}/{totalDevices} devices done)"
            : checkpoint.Phase.ToString();

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
                return;
            config.Jobs[index] = editor.Result;
            await _configService.SaveAsync();
            await RefreshAsync();
        };
        editor.Show();
        editor.Activate();
    }

    private void MoreActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
            return;

        menu.PlacementTarget = button;
        menu.IsOpen = true;
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
        await _jobRunService.DiscardCheckpointAsync(vm.Name);
        await RefreshAsync();
    }

    private async void JobEnabledToggle_Click(object sender, RoutedEventArgs e)
    {
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

        await _jobRunService.RunJobAsync(index);

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

        if (vm.PhaseText.Contains("Push", StringComparison.Ordinal))
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
            $"Job '{vm.Name}' was interrupted ({vm.CheckpointSummary}).\n\n" +
            "Discarding its checkpoint means the next run starts over from the very beginning " +
            "(a full re-pull and re-merge) instead of continuing where it left off.\n\n" +
            "Files already synced are not affected.",
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
            _ = _jobRunService.RunJobAsync(index, forcePush: true);
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
