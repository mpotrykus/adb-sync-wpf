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
        LoadFromSettings(config.Settings);
    }

    /// <summary>Populates every field except Projects Directory, which is left to the caller.</summary>
    private void LoadFromSettings(GlobalSettings settings)
    {
        StartAtLoginBox.IsChecked = settings.StartAtLogin;
        ShowInfoNotificationsBox.IsChecked = settings.ShowInfoNotifications;
        ShowErrorNotificationsBox.IsChecked = settings.ShowErrorNotifications;

        QuietHoursEnabledBox.IsChecked = settings.QuietHoursEnabled;
        QuietHoursStartBox.Text = settings.QuietHoursStart.ToString("HH:mm");
        QuietHoursEndBox.Text = settings.QuietHoursEnd.ToString("HH:mm");

        RunMissedSchedulesBox.IsChecked = settings.RunMissedSchedules;

        StaleLockHoursBox.Text = settings.StaleLockHours.ToString();
        LogRetentionDaysBox.Text = settings.LogRetentionDays.ToString();
        PerLogFileMaxMegabytesBox.Text = (settings.PerLogFileMaxBytes / (1024 * 1024)).ToString();
        ConflictRetentionDaysBox.Text = settings.ConflictRetentionDays.ToString();
        CheckpointRetentionDaysBox.Text = settings.CheckpointRetentionDays.ToString();
        MaxRunHistoryEntriesBox.Text = settings.MaxRunHistoryEntries.ToString();
        BandwidthThrottleKBpsBox.Text = settings.BandwidthThrottleKBps?.ToString() ?? "";
        RetryMaxAttemptsBox.Text = settings.RetryMaxAttempts.ToString();
        RetryBackoffSecondsBox.Text = settings.RetryBackoffSeconds.ToString();
        PushSafetyMinimumPercentBox.Text = settings.PushSafetyMinimumPercent.ToString();
        MaxConcurrentJobsBox.Text = settings.MaxConcurrentJobs.ToString();
        BackupConflictLosersBox.IsChecked = settings.BackupConflictLosers;
    }

    private void SetToDefault_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmDialog.Show(this, "Set to Default",
            "This resets every setting on this page to its default value. Your changes won't be saved unless you click Save afterward.",
            confirmText: "Set to Default"))
            LoadFromSettings(new GlobalSettings());
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

        _config.Settings.RunMissedSchedules = RunMissedSchedulesBox.IsChecked == true;

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
