using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AdbSync.App.Views;

public partial class PackageBrowserWindow : Window
{
    private readonly List<string> _allPackages;

    public string SelectedPackage { get; private set; } = "";

    public PackageBrowserWindow(IReadOnlyList<string> packages, string? deviceName = null)
    {
        InitializeComponent();
        _allPackages = packages.ToList();
        if (deviceName is not null)
            Title = $"Choose App Package - {deviceName}";

        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var filter = SearchBox.Text.Trim();
        var filtered = filter.Length == 0
            ? _allPackages
            : _allPackages.Where(p => p.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        PackagesList.ItemsSource = filtered;
        EmptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PackagesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PackagesList.SelectedItem is string package)
        {
            SelectedPackage = package;
            DialogResult = true;
        }
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (PackagesList.SelectedItem is not string package)
        {
            MessageBox.Show(this, "Select a package first.", "AdbSync", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectedPackage = package;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
