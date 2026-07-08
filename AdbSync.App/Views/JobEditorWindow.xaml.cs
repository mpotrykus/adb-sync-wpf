using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdbSync.Core.Config;
using AdbSync.Core.Devices;

namespace AdbSync.App.Views;

public partial class JobEditorWindow : Window
{
    private readonly ObservableCollection<JobDeviceBindingRow> _bindings = [];
    private readonly SyncJobConfig? _originalJob;
    private readonly List<DeviceConfig> _devices;
    private readonly IAdbDeviceResolver _deviceResolver;
    private readonly IDeviceChangeWatcher _changeWatcher;

    public SyncJobConfig? Result { get; private set; }

    public JobEditorWindow(AppConfig config, SyncJobConfig? job, IAdbDeviceResolver deviceResolver, IDeviceChangeWatcher changeWatcher)
    {
        InitializeComponent();
        _originalJob = job;
        _devices = config.Devices;
        _deviceResolver = deviceResolver;
        _changeWatcher = changeWatcher;

        DevicePicker.ItemsSource = config.Devices.Select(d => d.Name).ToList();
        DeviceBindingsList.ItemsSource = _bindings;
        _bindings.CollectionChanged += (_, _) => UpdateNoDevicesText();

        if (job is not null)
        {
            Title = $"Edit Job - {job.Name}";
            NameBox.Text = job.Name;
            NameBox.IsEnabled = false; // renaming would orphan the job's master folder/manifests on disk
            AppPackageBox.Text = job.AppPackage ?? "";
            ExcludeBox.Text = string.Join(Environment.NewLine, job.Exclude);
            foreach (var binding in job.Devices)
                _bindings.Add(new JobDeviceBindingRow(binding.DeviceName, binding.RemotePath));
            EnabledCheckBox.IsChecked = job.Enabled;

            switch (job.Schedule.Kind)
            {
                case ScheduleKind.Interval:
                    ScheduleInterval.IsChecked = true;
                    IntervalHoursBox.Text = job.Schedule.Interval.TotalHours.ToString("0.##");
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
        }
        else
        {
            Title = "New Job";
            EnabledCheckBox.IsChecked = true;
            ScheduleManual.IsChecked = true;
            IntervalHoursBox.Text = "4";
            DebounceSecondsBox.Text = "10";
        }

        UpdateNoDevicesText();
    }

    private void AddBinding_Click(object sender, RoutedEventArgs e)
    {
        if (DevicePicker.SelectedItem is not string deviceName || string.IsNullOrWhiteSpace(RemotePathBox.Text))
            return;

        _bindings.Add(new JobDeviceBindingRow(deviceName, RemotePathBox.Text.Trim()));
        RemotePathBox.Clear();
    }

    private void RemoveBinding_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is JobDeviceBindingRow row)
            _bindings.Remove(row);
    }

    private void UpdateNoDevicesText() =>
        NoDevicesText.Visibility = _bindings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show(this, "Name is required.", "AdbSync", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_bindings.Count == 0)
        {
            MessageBox.Show(this, "Add at least one device.", "AdbSync", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var schedule = BuildSchedule();
        // Preserve run history across edits - otherwise editing a job resets its interval clock to "due now".
        schedule.LastRunAt = _originalJob?.Schedule.LastRunAt;
        schedule.LastSuccessAt = _originalJob?.Schedule.LastSuccessAt;

        Result = new SyncJobConfig
        {
            Name = NameBox.Text.Trim(),
            AppPackage = string.IsNullOrWhiteSpace(AppPackageBox.Text) ? null : AppPackageBox.Text.Trim(),
            Exclude = ExcludeBox.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList(),
            Devices = _bindings.Select(b => new JobDeviceBinding { DeviceName = b.DeviceName, RemotePath = b.RemotePath }).ToList(),
            Schedule = schedule,
            Enabled = EnabledCheckBox.IsChecked == true,
        };
        DialogResult = true;
    }

    private JobSchedule BuildSchedule()
    {
        if (ScheduleInterval.IsChecked == true)
        {
            var hours = double.TryParse(IntervalHoursBox.Text, out var h) && h > 0 ? h : 4;
            return new JobSchedule { Kind = ScheduleKind.Interval, Interval = TimeSpan.FromHours(hours) };
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
            return new JobSchedule { Kind = ScheduleKind.OnChange, DebounceWindow = TimeSpan.FromSeconds(seconds) };
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
                    var availability = await _changeWatcher.CheckAvailabilityAsync(serial, binding.RemotePath);
                    results.Add(availability.LiveWatchSupported
                        ? new WatchTestResultRow(header, "Live watch supported.", SuccessBrush)
                        : new WatchTestResultRow(header, $"Not supported - {availability.Detail}. Will use 60s polling instead.", WarningBrush));
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

    private static Brush SuccessBrush => (Brush)Application.Current.Resources["Brush.Success"];
    private static Brush WarningBrush => (Brush)Application.Current.Resources["Brush.Warning"];
    private static Brush DangerBrush => (Brush)Application.Current.Resources["Brush.Danger"];

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed record JobDeviceBindingRow(string DeviceName, string RemotePath)
{
    public override string ToString() => $"{DeviceName} → {RemotePath}";
}

public sealed record WatchTestResultRow(string Header, string Detail, Brush StatusBrush);
