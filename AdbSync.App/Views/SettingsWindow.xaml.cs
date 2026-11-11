using AdbSync.App.Services;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace AdbSync.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    public bool Saved { get; private set; }

    private readonly Func<bool> _anyJobRunning;

    public SettingsWindow(AppConfig config, Func<bool>? anyJobRunning = null)
    {
        InitializeComponent();
        _config = config;
        _anyJobRunning = anyJobRunning ?? (() => false);

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

        CompanionBroadcastsEnabledBox.IsChecked = settings.CompanionBroadcastsEnabled;
        CompanionServerEnabledBox.IsChecked = settings.CompanionServerEnabled;
        CompanionServerPortBox.Text = settings.CompanionServerPort.ToString();

        StaleLockHoursBox.Text = settings.StaleLockHours.ToString();
        LogRetentionDaysBox.Text = settings.LogRetentionDays.ToString();
        PerLogFileMaxMegabytesBox.Text = (settings.PerLogFileMaxBytes / (1024 * 1024)).ToString();
        ConflictRetentionDaysBox.Text = settings.ConflictRetentionDays.ToString();
        CheckpointRetentionDaysBox.Text = settings.CheckpointRetentionDays.ToString();
        MaxRunHistoryEntriesBox.Text = settings.MaxRunHistoryEntries.ToString();
        FileDetailRunsKeptBox.Text = settings.FileDetailRunsKept.ToString();
        RollbackPointsToKeepBox.Text = settings.RollbackPointsToKeep.ToString();
        BandwidthThrottleKBpsBox.Text = settings.BandwidthThrottleKBps?.ToString() ?? "";
        RetryMaxAttemptsBox.Text = settings.RetryMaxAttempts.ToString();
        RetryBackoffSecondsBox.Text = settings.RetryBackoffSeconds.ToString();
        PushSafetyMinimumPercentBox.Text = settings.PushSafetyMinimumPercent.ToString();
        MaxConcurrentJobsBox.Text = settings.MaxConcurrentJobs.ToString();
        MaxConcurrentPerDeviceBox.Text = settings.MaxConcurrentPerDevice.ToString();
        BackupConflictLosersBox.IsChecked = settings.BackupConflictLosers;
        AskOnConflictBox.IsChecked = settings.ConflictPolicy == ConflictPolicy.Ask;
    }

    private void SetToDefault_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmDialog.Show(this, "Set to Default",
            "This resets every setting on this page to its default value. Your changes won't be saved unless you click Save afterward.",
            confirmText: "Set to Default"))
        {
            LoadFromSettings(new GlobalSettings { ProjectsDirectory = AppPaths.Default.DefaultProjectsDirectory });
            ProjectsDirectoryErrorText.Visibility = Visibility.Collapsed;
        }
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
        ProjectsDirectoryErrorText.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(ProjectsDirectoryBox.Text))
        {
            ProjectsDirectoryErrorText.Text = "Projects directory is required.";
            ProjectsDirectoryErrorText.Visibility = Visibility.Visible;
            return;
        }

        var newRoot = ProjectsDirectoryBox.Text.Trim();
        if (!Path.IsPathFullyQualified(newRoot))
        {
            ProjectsDirectoryErrorText.Text = @"Enter a full path, for example D:\AdbSync Projects.";
            ProjectsDirectoryErrorText.Visibility = Visibility.Visible;
            return;
        }
        var oldRoot = _config.Settings.ProjectsDirectory;
        if (!string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase) && !TryMigrateProjects(oldRoot, newRoot))
            return;

        _config.Settings.ProjectsDirectory = newRoot;
        _config.Settings.StartAtLogin = StartAtLoginBox.IsChecked == true;
        _config.Settings.ShowInfoNotifications = ShowInfoNotificationsBox.IsChecked == true;
        _config.Settings.ShowErrorNotifications = ShowErrorNotificationsBox.IsChecked == true;

        _config.Settings.QuietHoursEnabled = QuietHoursEnabledBox.IsChecked == true;
        if (TimeOnly.TryParse(QuietHoursStartBox.Text, out var quietStart))
            _config.Settings.QuietHoursStart = quietStart;
        if (TimeOnly.TryParse(QuietHoursEndBox.Text, out var quietEnd))
            _config.Settings.QuietHoursEnd = quietEnd;

        _config.Settings.RunMissedSchedules = RunMissedSchedulesBox.IsChecked == true;

        _config.Settings.CompanionBroadcastsEnabled = CompanionBroadcastsEnabledBox.IsChecked == true;
        _config.Settings.CompanionServerEnabled = CompanionServerEnabledBox.IsChecked == true;
        if (int.TryParse(CompanionServerPortBox.Text, out var companionPort) && companionPort is >= 1024 and <= 65535)
            _config.Settings.CompanionServerPort = companionPort;

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
        if (int.TryParse(FileDetailRunsKeptBox.Text, out var fileDetailRunsKept) && fileDetailRunsKept >= 0)
            _config.Settings.FileDetailRunsKept = fileDetailRunsKept;
        if (int.TryParse(RollbackPointsToKeepBox.Text, out var rollbackPointsToKeep) && rollbackPointsToKeep >= 0)
            _config.Settings.RollbackPointsToKeep = rollbackPointsToKeep;
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
        if (int.TryParse(MaxConcurrentPerDeviceBox.Text, out var maxConcurrentPerDevice) && maxConcurrentPerDevice > 0)
            _config.Settings.MaxConcurrentPerDevice = maxConcurrentPerDevice;
        _config.Settings.BackupConflictLosers = BackupConflictLosersBox.IsChecked == true;
        _config.Settings.ConflictPolicy = AskOnConflictBox.IsChecked == true ? ConflictPolicy.Ask : ConflictPolicy.NewestWins;

        StartupRegistration.SetEnabled(_config.Settings.StartAtLogin);

        Saved = true;
        Close();
    }

    /// <returns>False if the save should be aborted.</returns>
    private bool TryMigrateProjects(string oldRoot, string newRoot)
    {
        if (ProjectsDirectoryMigrator.IsSameOrNested(oldRoot, newRoot))
        {
            ProjectsDirectoryErrorText.Text = "The new directory can't be inside the old one (or vice versa).";
            ProjectsDirectoryErrorText.Visibility = Visibility.Visible;
            return false;
        }

        var plan = ProjectsDirectoryMigrator.Plan(_config.Jobs, oldRoot, newRoot);
        if (plan.JobFolders.Count == 0)
            return true;

        if (plan.Conflicts.Count > 0)
        {
            ProjectsDirectoryErrorText.Text =
                $"Can't move: the new directory already contains folders for {string.Join(", ", plan.Conflicts)}.";
            ProjectsDirectoryErrorText.Visibility = Visibility.Visible;
            return false;
        }

        var result = ConfirmDialog.ShowWithResult(this, "Move projects?",
            $"Move {plan.JobFolders.Count} existing project folder(s) from\n{oldRoot}\nto\n{newRoot}?\n\n" +
            "Choose \"Don't move\" to change the setting only; jobs will then see empty projects until you move the folders yourself.",
            confirmText: "Move", secondaryText: "Don't move");
        if (result == ConfirmDialogResult.Cancel)
            return false;
        if (result == ConfirmDialogResult.Secondary)
            return true;

        if (_anyJobRunning())
        {
            ProjectsDirectoryErrorText.Text = "Can't move projects while a job is running. Wait for it to finish and try again.";
            ProjectsDirectoryErrorText.Visibility = Visibility.Visible;
            return false;
        }

        try
        {
            ProjectsDirectoryMigrator.Move(plan, oldRoot, newRoot);
            return true;
        }
        catch (Exception ex)
        {
            ProjectsDirectoryErrorText.Text = $"Move failed: {ex.Message}. Some folders may already have moved.";
            ProjectsDirectoryErrorText.Visibility = Visibility.Visible;
            return false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
