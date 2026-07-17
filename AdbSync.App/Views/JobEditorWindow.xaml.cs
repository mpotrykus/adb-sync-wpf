using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Transfer;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AdbSync.App.Views;

public partial class JobEditorWindow : Window
{
    private readonly ObservableCollection<JobDeviceBindingRow> _bindings = [];
    private readonly SyncJobConfig? _originalJob;
    private readonly List<DeviceConfig> _devices;
    private readonly IAdbDeviceResolver _deviceResolver;
    private readonly IDeviceChangeWatcher _changeWatcher;
    private readonly IRemoteFileSystemFactory _remoteFileSystemFactory;
    private readonly IDevicePackageLister _packageLister;

    public SyncJobConfig? Result { get; private set; }

    public JobEditorWindow(
        AppConfig config, SyncJobConfig? job, IAdbDeviceResolver deviceResolver, IDeviceChangeWatcher changeWatcher,
        IRemoteFileSystemFactory remoteFileSystemFactory, IDevicePackageLister packageLister)
    {
        InitializeComponent();
        _originalJob = job;
        _devices = config.Devices;
        _deviceResolver = deviceResolver;
        _changeWatcher = changeWatcher;
        _remoteFileSystemFactory = remoteFileSystemFactory;
        _packageLister = packageLister;

        DevicePicker.ItemsSource = config.Devices.Select(d => d.Name).ToList();
        DeviceBindingsList.ItemsSource = _bindings;
        _bindings.CollectionChanged += (_, _) => UpdateDeviceDependentUi();

        if (job is not null)
        {
            Title = $"Edit Job - {job.Name}";
            NameBox.Text = job.Name;
            NameBox.IsEnabled = false;
            AppPackageBox.Text = job.AppPackage ?? "";
            ProjectDirectoryBox.Text = job.ProjectDirectory ?? "";
            ExcludeBox.Text = string.Join(Environment.NewLine, job.Exclude);
            foreach (var binding in job.Devices)
                _bindings.Add(new JobDeviceBindingRow(binding.DeviceName, binding.RemotePath));
            EnabledCheckBox.IsChecked = job.Enabled;

            IntervalUnitCombo.SelectedIndex = 2;
            switch (job.Schedule.Kind)
            {
                case ScheduleKind.Interval:
                    ScheduleInterval.IsChecked = true;
                    var (intervalValue, intervalUnitIndex) = DecomposeInterval(job.Schedule.Interval);
                    IntervalValueBox.Text = intervalValue.ToString("0.##");
                    IntervalUnitCombo.SelectedIndex = intervalUnitIndex;
                    break;
                case ScheduleKind.DailyAt:
                    ScheduleDailyAt.IsChecked = true;
                    DailyTimesBox.Text = string.Join(", ", job.Schedule.DailyTimes.Select(t => t.ToString("HH:mm")));
                    break;
                case ScheduleKind.OnChange:
                    ScheduleOnChange.IsChecked = true;
                    DebounceSecondsBox.Text = job.Schedule.DebounceWindow.TotalSeconds.ToString("0.##");
                    break;
                default:
                    ScheduleManual.IsChecked = true;
                    break;
            }
            RescanIntervalMinutesBox.Text = job.Schedule.RescanInterval.TotalMinutes.ToString("0.##");

            StaleLockHoursOverrideBox.Text = job.StaleLockHours?.ToString() ?? "";
            ConflictRetentionDaysOverrideBox.Text = job.ConflictRetentionDays?.ToString() ?? "";
            CheckpointRetentionDaysOverrideBox.Text = job.CheckpointRetentionDays?.ToString() ?? "";
            MaxRunHistoryEntriesOverrideBox.Text = job.MaxRunHistoryEntries?.ToString() ?? "";
            BandwidthThrottleKBpsOverrideBox.Text = job.BandwidthThrottleKBps?.ToString() ?? "";
            PushSafetyMinimumPercentOverrideBox.Text = job.PushSafetyMinimumPercent?.ToString() ?? "";
            RetryMaxAttemptsOverrideBox.Text = job.RetryMaxAttempts?.ToString() ?? "";
            RetryBackoffSecondsOverrideBox.Text = job.RetryBackoffSeconds?.ToString() ?? "";
            ShowInfoNotificationsOverrideBox.IsChecked = job.ShowInfoNotifications;
            ShowErrorNotificationsOverrideBox.IsChecked = job.ShowErrorNotifications;
            BackupConflictLosersOverrideBox.IsChecked = job.BackupConflictLosers;
            DryRunBox.IsChecked = job.DryRun;
        }
        else
        {
            Title = "New Job";
            EnabledCheckBox.IsChecked = true;
            ScheduleManual.IsChecked = true;
            IntervalValueBox.Text = "4";
            IntervalUnitCombo.SelectedIndex = 2;
            DebounceSecondsBox.Text = "10";
            RescanIntervalMinutesBox.Text = "15";
            ShowInfoNotificationsOverrideBox.IsChecked = null;
            ShowErrorNotificationsOverrideBox.IsChecked = null;
            BackupConflictLosersOverrideBox.IsChecked = null;
        }

        UpdateDeviceDependentUi();
    }

    private void AddBinding_Click(object sender, RoutedEventArgs e)
    {
        if (DevicePicker.SelectedItem is not string deviceName || string.IsNullOrWhiteSpace(RemotePathBox.Text))
            return;

        _bindings.Add(new JobDeviceBindingRow(deviceName, RemotePathBox.Text.Trim()));
        RemotePathBox.Clear();
    }

    private async void BrowseRemotePath_Click(object sender, RoutedEventArgs e)
    {
        if (DevicePicker.SelectedItem is not string deviceName)
        {
            MessageBox.Show(this, "Select a device first.", "AdbSync", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var device = _devices.FirstOrDefault(d => d.Name == deviceName);
        if (device is null)
            return;

        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            var serial = await _deviceResolver.EnsureConnectedAsync(device);
            var remoteFileSystem = _remoteFileSystemFactory.Create(serial);
            try
            {
                var startPath = string.IsNullOrWhiteSpace(RemotePathBox.Text) ? "/sdcard" : RemotePathBox.Text.Trim();
                var browser = new DeviceFolderBrowserWindow(remoteFileSystem, startPath, deviceName) { Owner = this };
                if (browser.ShowDialog() == true)
                    RemotePathBox.Text = browser.SelectedPath;
            }
            finally
            {
                if (remoteFileSystem is IAsyncDisposable disposable)
                    await disposable.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not connect to device - {ex.Message}", "AdbSync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void BrowseProjectDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(ProjectDirectoryBox.Text) ? ProjectDirectoryBox.Text : null,
        };
        if (dialog.ShowDialog(this) == true)
            ProjectDirectoryBox.Text = dialog.FolderName;
    }

    private void RemoveBinding_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is JobDeviceBindingRow row)
            _bindings.Remove(row);
    }

    private void UpdateDeviceDependentUi()
    {
        NoDevicesText.Visibility = _bindings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BrowseAppPackageButton.IsEnabled = _bindings.Count > 0;
        if (_bindings.Count > 0)
            DevicesErrorText.Visibility = Visibility.Collapsed;
    }

    private async void BrowseAppPackage_Click(object sender, RoutedEventArgs e)
    {
        var deviceNames = _bindings.Select(b => b.DeviceName).Distinct().ToList();
        if (deviceNames.Count == 0)
            return;

        string deviceName;
        if (deviceNames.Count == 1)
        {
            deviceName = deviceNames[0];
        }
        else
        {
            var picker = new SelectDeviceWindow(deviceNames) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedDeviceName is null)
                return;
            deviceName = picker.SelectedDeviceName;
        }

        var device = _devices.FirstOrDefault(d => d.Name == deviceName);
        if (device is null)
            return;

        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            var serial = await _deviceResolver.EnsureConnectedAsync(device);
            var packages = await _packageLister.ListInstalledPackagesAsync(serial);
            var browser = new PackageBrowserWindow(packages, deviceName) { Owner = this };
            if (browser.ShowDialog() == true)
                AppPackageBox.Text = browser.SelectedPackage;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not list packages - {ex.Message}", "AdbSync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        NameErrorText.Visibility = Visibility.Collapsed;
        DevicesErrorText.Visibility = Visibility.Collapsed;

        var hasError = false;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            NameErrorText.Text = "Name is required.";
            NameErrorText.Visibility = Visibility.Visible;
            hasError = true;
        }
        if (_bindings.Count == 0)
        {
            DevicesErrorText.Text = "Add at least one device.";
            DevicesErrorText.Visibility = Visibility.Visible;
            hasError = true;
        }
        if (hasError)
            return;

        var schedule = BuildSchedule();
        schedule.LastRunAt = _originalJob?.Schedule.LastRunAt;
        schedule.LastSuccessAt = _originalJob?.Schedule.LastSuccessAt;

        Result = new SyncJobConfig
        {
            Name = NameBox.Text.Trim(),
            AppPackage = string.IsNullOrWhiteSpace(AppPackageBox.Text) ? null : AppPackageBox.Text.Trim(),
            ProjectDirectory = string.IsNullOrWhiteSpace(ProjectDirectoryBox.Text) ? null : ProjectDirectoryBox.Text.Trim(),
            Exclude = ParseExcludePatterns(),
            Devices = _bindings.Select(b => new JobDeviceBinding { DeviceName = b.DeviceName, RemotePath = b.RemotePath }).ToList(),
            Schedule = schedule,
            Enabled = EnabledCheckBox.IsChecked == true,
            StaleLockHours = ParseIntOrNull(StaleLockHoursOverrideBox.Text),
            ConflictRetentionDays = ParseIntOrNull(ConflictRetentionDaysOverrideBox.Text),
            CheckpointRetentionDays = ParseIntOrNull(CheckpointRetentionDaysOverrideBox.Text),
            MaxRunHistoryEntries = ParseIntOrNull(MaxRunHistoryEntriesOverrideBox.Text),
            BandwidthThrottleKBps = ParseIntOrNull(BandwidthThrottleKBpsOverrideBox.Text),
            PushSafetyMinimumPercent = ParseIntOrNull(PushSafetyMinimumPercentOverrideBox.Text),
            RetryMaxAttempts = ParseIntOrNull(RetryMaxAttemptsOverrideBox.Text),
            RetryBackoffSeconds = ParseIntOrNull(RetryBackoffSecondsOverrideBox.Text),
            ShowInfoNotifications = ShowInfoNotificationsOverrideBox.IsChecked,
            ShowErrorNotifications = ShowErrorNotificationsOverrideBox.IsChecked,
            BackupConflictLosers = BackupConflictLosersOverrideBox.IsChecked,
            DryRun = DryRunBox.IsChecked == true,
        };
        Close();
    }

    private static int? ParseIntOrNull(string text) => int.TryParse(text, out var value) ? value : null;

    private static readonly double[] IntervalUnitSeconds = [1, 60, 3600, 86400, 604800, 2_592_000, 31_536_000];

    private static (double Value, int UnitIndex) DecomposeInterval(TimeSpan interval)
    {
        var totalSeconds = interval.TotalSeconds;
        for (var i = IntervalUnitSeconds.Length - 1; i >= 0; i--)
        {
            var value = totalSeconds / IntervalUnitSeconds[i];
            if (Math.Abs(value - Math.Round(value)) < 0.0001)
                return (Math.Round(value), i);
        }
        return (totalSeconds, 0);
    }

    private JobSchedule BuildSchedule()
    {
        if (ScheduleInterval.IsChecked == true)
        {
            var value = double.TryParse(IntervalValueBox.Text, out var v) && v > 0 ? v : 4;
            var unitIndex = IntervalUnitCombo.SelectedIndex >= 0 ? IntervalUnitCombo.SelectedIndex : 2;
            var interval = TimeSpan.FromSeconds(value * IntervalUnitSeconds[unitIndex]);
            return new JobSchedule { Kind = ScheduleKind.Interval, Interval = interval };
        }

        if (ScheduleDailyAt.IsChecked == true)
        {
            var times = DailyTimesBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => TimeOnly.TryParse(s, out var t) ? (TimeOnly?)t : null)
                .Where(t => t is not null)
                .Select(t => t!.Value)
                .ToList();
            return new JobSchedule { Kind = ScheduleKind.DailyAt, DailyTimes = times };
        }

        if (ScheduleOnChange.IsChecked == true)
        {
            var seconds = double.TryParse(DebounceSecondsBox.Text, out var s) && s > 0 ? s : 10;
            var rescanMinutes = double.TryParse(RescanIntervalMinutesBox.Text, out var m) && m > 0 ? m : 15;
            return new JobSchedule
            {
                Kind = ScheduleKind.OnChange,
                DebounceWindow = TimeSpan.FromSeconds(seconds),
                RescanInterval = TimeSpan.FromMinutes(rescanMinutes),
            };
        }

        return new JobSchedule { Kind = ScheduleKind.Manual };
    }

    private async void TestWatch_Click(object sender, RoutedEventArgs e)
    {
        if (_bindings.Count == 0)
        {
            MessageBox.Show(this, "Add at least one device first.", "AdbSync", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        TestWatchButton.IsEnabled = false;
        TestResultsList.ItemsSource = null;
        try
        {
            var exclude = new ExcludeMatcher(ParseExcludePatterns());
            var results = new List<WatchTestResultRow>();
            foreach (var binding in _bindings)
            {
                var header = $"{binding.DeviceName} → {binding.RemotePath}";
                var device = _devices.FirstOrDefault(d => d.Name == binding.DeviceName);
                if (device is null)
                {
                    results.Add(new WatchTestResultRow(header, "Device not found.", DangerBrush));
                    continue;
                }

                try
                {
                    var serial = await _deviceResolver.EnsureConnectedAsync(device);
                    var availability = await _changeWatcher.CheckAvailabilityAsync(serial, binding.RemotePath, exclude);
                    results.Add(availability switch
                    {
                        { LiveWatchSupported: false } =>
                            new WatchTestResultRow(header, $"Not supported - {availability.Detail}. Will use 60s polling instead.", WarningBrush),
                        { Warning: not null } =>
                            new WatchTestResultRow(header, $"Live watch supported, but {availability.Warning}", WarningBrush),
                        _ => new WatchTestResultRow(header, "Live watch supported.", SuccessBrush),
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new WatchTestResultRow(header, $"Could not connect - {ex.Message}", DangerBrush));
                }
            }

            TestResultsList.ItemsSource = results;
        }
        finally
        {
            TestWatchButton.IsEnabled = true;
        }
    }

    private List<string> ParseExcludePatterns() => ExcludeBox.Text
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim())
        .Where(s => s.Length > 0)
        .ToList();

    private static Brush SuccessBrush => (Brush)Application.Current.Resources["Brush.Success"];
    private static Brush WarningBrush => (Brush)Application.Current.Resources["Brush.Warning"];
    private static Brush DangerBrush => (Brush)Application.Current.Resources["Brush.Danger"];

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed record JobDeviceBindingRow(string DeviceName, string RemotePath)
{
    public override string ToString() => $"{DeviceName} → {RemotePath}";
}

public sealed record WatchTestResultRow(string Header, string Detail, Brush StatusBrush);
