using System.Windows;
using System.Windows.Input;

namespace AdbSync.App.Views;

public partial class SelectDeviceWindow : Window
{
    public string? SelectedDeviceName { get; private set; }

    public SelectDeviceWindow(IReadOnlyList<string> deviceNames)
    {
        InitializeComponent();
        DevicesList.ItemsSource = deviceNames;
        DevicesList.SelectedIndex = 0;
    }

    private void DevicesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DevicesList.SelectedItem is string name)
        {
            SelectedDeviceName = name;
            DialogResult = true;
        }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesList.SelectedItem is not string name)
        {
            MessageBox.Show(this, "Select a device first.", "AdbSync", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectedDeviceName = name;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
