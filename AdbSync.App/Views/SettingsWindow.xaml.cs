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
        StaleLockHoursBox.Text = config.Settings.StaleLockHours.ToString();
        LogRetentionDaysBox.Text = config.Settings.LogRetentionDays.ToString();
        ConflictRetentionDaysBox.Text = config.Settings.ConflictRetentionDays.ToString();
        MaxConcurrentJobsBox.Text = config.Settings.MaxConcurrentJobs.ToString();
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
        if (int.TryParse(StaleLockHoursBox.Text, out var staleHours) && staleHours > 0)
            _config.Settings.StaleLockHours = staleHours;
        if (int.TryParse(LogRetentionDaysBox.Text, out var retentionDays) && retentionDays > 0)
            _config.Settings.LogRetentionDays = retentionDays;
        if (int.TryParse(ConflictRetentionDaysBox.Text, out var conflictRetentionDays) && conflictRetentionDays > 0)
            _config.Settings.ConflictRetentionDays = conflictRetentionDays;

        StartupRegistration.SetEnabled(_config.Settings.StartAtLogin);

        Saved = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
