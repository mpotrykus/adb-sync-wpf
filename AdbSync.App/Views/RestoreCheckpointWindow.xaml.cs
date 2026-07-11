using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdbSync.App.ViewModels;
using AdbSync.Core.Models.Orchestration;

namespace AdbSync.App.Views;

public partial class RestoreCheckpointWindow : Window
{
    public SnapshotInfo? SelectedSnapshot { get; private set; }

    public RestoreCheckpointWindow(string jobName, IReadOnlyList<SnapshotInfo> snapshots)
    {
        InitializeComponent();
        Title = $"Restore Checkpoint - {jobName}";
        SnapshotsGrid.ItemsSource = snapshots.Select(s => new SnapshotRowViewModel(s)).ToList();
    }

    private void SnapshotsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RestoreButton.IsEnabled = SnapshotsGrid.SelectedItem is not null;

    private void SnapshotsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SnapshotsGrid.SelectedItem is not null)
            Restore_Click(sender, e);
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsGrid.SelectedItem is not SnapshotRowViewModel row)
            return;

        SelectedSnapshot = row.Snapshot;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SnapshotRowViewModel row })
            return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{row.Snapshot.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open the checkpoint folder: {ex.Message}", "Open Folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
