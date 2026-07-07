using System.Collections.ObjectModel;
using System.Windows;
using AdbSync.Core.Config;

namespace AdbSync.App.Views;

public partial class JobEditorWindow : Window
{
    private readonly ObservableCollection<JobDeviceBindingRow> _bindings = [];
    private readonly SyncJobConfig? _originalJob;

    public SyncJobConfig? Result { get; private set; }

    public JobEditorWindow(AppConfig config, SyncJobConfig? job)
    {
        InitializeComponent();
        _originalJob = job;

        DevicePicker.ItemsSource = config.Devices.Select(d => d.Name).ToList();
        DeviceBindingsList.ItemsSource = _bindings;

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
        }
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
        if (DeviceBindingsList.SelectedItem is JobDeviceBindingRow row)
            _bindings.Remove(row);
    }

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

        return new JobSchedule { Kind = ScheduleKind.Manual };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed record JobDeviceBindingRow(string DeviceName, string RemotePath)
{
    public override string ToString() => $"{DeviceName} → {RemotePath}";
}
