using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdbSync.Core.Models.Transfer;
using AdbSync.Core.Services.Transfer;

namespace AdbSync.App.Views;

public partial class DeviceFolderBrowserWindow : Window
{
    private readonly IRemoteFileSystem _remoteFileSystem;
    private string _currentPath;
    private List<RemoteFolderRow> _allFolders = new();

    public string SelectedPath { get; private set; } = "/";

    public DeviceFolderBrowserWindow(IRemoteFileSystem remoteFileSystem, string initialPath, string? deviceName = null)
    {
        InitializeComponent();
        _remoteFileSystem = remoteFileSystem;
        _currentPath = Normalize(initialPath);
        if (deviceName is not null)
            Title = $"Choose Folder - {deviceName}";

        Loaded += async (_, _) => await NavigateAsync(_currentPath);
    }

    private async void FoldersList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not RemoteFolderRow row)
            return;

        await NavigateAsync(row.IsParent ? GetParentPath(_currentPath) : JoinPath(_currentPath, row.Name));
    }

    private async void Go_Click(object sender, RoutedEventArgs e) => await NavigateAsync(PathBox.Text);

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true; // otherwise Enter also activates the IsDefault "Select Folder" button
        await NavigateAsync(PathBox.Text);
    }

    private async Task NavigateAsync(string path)
    {
        var normalized = Normalize(path);
        SetStatus(null);
        FoldersList.IsEnabled = false;
        try
        {
            var children = await _remoteFileSystem.ListDirectoryAsync(normalized);
            var folders = children
                .Where(c => c.IsDirectory)
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new RemoteFolderRow(c.Name))
                .ToList();

            _currentPath = normalized;
            PathBox.Text = normalized;
            _allFolders = folders;
            FilterBox.Text = "";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not list '{normalized}' - {ex.Message}");
            PathBox.Text = _currentPath;
        }
        finally
        {
            FoldersList.IsEnabled = true;
        }
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var filter = FilterBox.Text.Trim();
        var filtered = filter.Length == 0
            ? _allFolders
            : _allFolders.Where(f => f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        var display = new List<RemoteFolderRow>();
        if (_currentPath != "/")
            display.Add(new RemoteFolderRow("..", IsParent: true));
        display.AddRange(filtered);

        FoldersList.ItemsSource = display;
        EmptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _allFolders.Count == 0 ? "No subfolders." : "No matching folders.";
    }

    private void SetStatus(string? message)
    {
        StatusText.Text = message ?? "";
        StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        SelectedPath = _currentPath;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string Normalize(string path)
    {
        var trimmed = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        if (trimmed[0] != '/')
            trimmed = "/" + trimmed;
        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }

    private static string JoinPath(string basePath, string name) => basePath == "/" ? $"/{name}" : $"{basePath}/{name}";

    private static string GetParentPath(string path)
    {
        if (path == "/")
            return "/";
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? "/" : path[..idx];
    }
}

public sealed record RemoteFolderRow(string Name, bool IsParent = false);
