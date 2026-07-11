using System.IO;
using System.Windows;
using AdbSync.App.Services;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using Microsoft.Win32;

namespace AdbSync.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    public bool Saved { get; private set; }

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;

        ProjectsDirectoryBox.Text = config.Settings.ProjectsDirectory;
        StartAtLoginBox.IsChecked = config.Settings.StartAtLogin;
        ShowInfoNotificationsBox.IsChecked = config.Settings.ShowInfoNotifications;
        ShowErrorNotificationsBox.IsChecked = config.Settings.ShowErrorNotifications;

        QuietHoursEnabledBox.IsChecked = config.Settings.QuietHoursEnabled;
        QuietHoursStartBox.Text = config.Settings.QuietHoursStart.ToString("HH:mm");
        QuietHoursEndBox.Text = config.Settings.QuietHoursEnd.ToString("HH:mm");

        StaleLockHoursBox.Text = config.Settings.StaleLockHours.ToString();
        LogRetentionDaysBox.Text = config.Settings.LogRetentionDays.ToString();
        PerLogFileMaxMegabytesBox.Text = (config.Settings.PerLogFileMaxBytes / (1024 * 1024)).ToString();
        ConflictRetentionDaysBox.Text = config.Settings.ConflictRetentionDays.ToString();
        CheckpointRetentionDaysBox.Text = config.Settings.CheckpointRetentionDays.ToString();
        MaxRunHistoryEntriesBox.Text = config.Settings.MaxRunHistoryEntries.ToString();
        BandwidthThrottleKBpsBox.Text = config.Settings.BandwidthThrottleKBps?.ToString() ?? "";
        RetryMaxAttemptsBox.Text = config.Settings.RetryMaxAttempts.ToString();
        RetryBackoffSecondsBox.Text = config.Settings.RetryBackoffSeconds.ToString();
        PushSafetyMinimumPercentBox.Text = config.Settings.PushSafetyMinimumPercent.ToString();
        MaxConcurrentJobsBox.Text = config.Settings.MaxConcurrentJobs.ToString();
        BackupConflictLosersBox.IsChecked = config.Settings.BackupConflictLosers;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(ProjectsDirectoryBox.Text) ? ProjectsDirectoryBox.Text : null,
        };
        if (dialog.ShowDialog(this) == true)
            ProjectsDirectoryBox.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProjectsDirectoryBox.Text))
        {
            MessageBox.Show(this, "Projects directory is required.", "AdbSync", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _config.Settings.ProjectsDirectory = ProjectsDirectoryBox.Text.Trim();
        _config.Settings.StartAtLogin = StartAtLoginBox.IsChecked == true;
        _config.Settings.ShowInfoNotifications = ShowInfoNotificationsBox.IsChecked == true;
        _config.Settings.ShowErrorNotifications = ShowErrorNotificationsBox.IsChecked == true;

        _config.Settings.QuietHoursEnabled = QuietHoursEnabledBox.IsChecked == true;
        if (TimeOnly.TryParse(QuietHoursStartBox.Text, out var quietStart))
            _config.Settings.QuietHoursStart = quietStart;
        if (TimeOnly.TryParse(QuietHoursEndBox.Text, out var quietEnd))
            _config.Settings.QuietHoursEnd = quietEnd;

        if (int.TryParse(StaleLockHoursBox.Text, out var staleHours) && staleHours > 0)
            _config.Settings.StaleLockHours = staleHours;
        if (int.TryParse(LogRetentionDaysBox.Text, out var retentionDays) && retentionDays > 0)
            _config.Settings.LogRetentionDays = retentionDays;
        if (int.TryParse(PerLogFileMaxMegabytesBox.Text, out var maxLogMegabytes) && maxLogMegabytes > 0)
            _config.Settings.PerLogFileMaxBytes = maxLogMegabytes * 1024L * 1024L;
        if (int.TryParse(ConflictRetentionDaysBox.Text, out var conflictRetentionDays) && conflictRetentionDays > 0)
            _config.Settings.ConflictRetentionDays = conflictRetentionDays;
        if (int.TryParse(CheckpointRetentionDaysBox.Text, out var checkpointRetentionDays) && checkpointRetentionDays > 0)
            _config.Settings.CheckpointRetentionDays = checkpointRetentionDays;
        if (int.TryParse(MaxRunHistoryEntriesBox.Text, out var maxRunHistoryEntries) && maxRunHistoryEntries > 0)
            _config.Settings.MaxRunHistoryEntries = maxRunHistoryEntries;
        _config.Settings.BandwidthThrottleKBps = int.TryParse(BandwidthThrottleKBpsBox.Text, out var throttle) && throttle > 0
            ? throttle
            : null;
        if (int.TryParse(RetryMaxAttemptsBox.Text, out var retryMaxAttempts) && retryMaxAttempts > 0)
            _config.Settings.RetryMaxAttempts = retryMaxAttempts;
        if (int.TryParse(RetryBackoffSecondsBox.Text, out var retryBackoffSeconds) && retryBackoffSeconds >= 0)
            _config.Settings.RetryBackoffSeconds = retryBackoffSeconds;
        if (int.TryParse(PushSafetyMinimumPercentBox.Text, out var pushSafetyMinimumPercent) && pushSafetyMinimumPercent is > 0 and <= 100)
            _config.Settings.PushSafetyMinimumPercent = pushSafetyMinimumPercent;
        if (int.TryParse(MaxConcurrentJobsBox.Text, out var maxConcurrentJobs) && maxConcurrentJobs > 0)
            _config.Settings.MaxConcurrentJobs = maxConcurrentJobs;
        _config.Settings.BackupConflictLosers = BackupConflictLosersBox.IsChecked == true;

        StartupRegistration.SetEnabled(_config.Settings.StartAtLogin);

        Saved = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
