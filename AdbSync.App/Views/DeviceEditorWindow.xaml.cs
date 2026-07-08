using System.Windows;
using System.Windows.Controls;
using AdbSync.Core.Config;
using AdbSync.Core.Devices;
using AdvancedSharpAdbClient;

namespace AdbSync.App.Views;

public partial class DeviceEditorWindow : Window
{
    private readonly AppConfig _config;
    private DeviceConfig? _selected;
    private bool _changed;

    public DeviceEditorWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        RefreshList();
    }

    private void RefreshList(string? selectName = null)
    {
        DeviceList.ItemsSource = _config.Devices.Select(d => d.Name).ToList();
        if (selectName is not null)
            DeviceList.SelectedItem = selectName;
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not string name)
        {
            _selected = null;
            return;
        }

        _selected = _config.Devices.FirstOrDefault(d => d.Name == name);
        if (_selected is null)
            return;

        NameBox.Text = _selected.Name;
        NameBox.IsEnabled = false; // renaming would break job device-bindings referencing this name
        if (_selected.Ip is not null)
        {
            WifiRadio.IsChecked = true;
            IpOrSerialBox.Text = _selected.Ip;
        }
        else
        {
            UsbRadio.IsChecked = true;
            IpOrSerialBox.Text = _selected.Serial ?? "";
        }
        TestResultText.Text = "";
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _selected = null;
        DeviceList.SelectedItem = null;
        NameBox.Text = "";
        NameBox.IsEnabled = true;
        WifiRadio.IsChecked = true;
        IpOrSerialBox.Text = "";
        TestResultText.Text = "";
    }

    private void SaveDevice_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Name is required.", "AdbSync", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var isNew = _selected is null;
        if (isNew && _config.Devices.Any(d => d.Name == name))
        {
            MessageBox.Show(this, $"A device named '{name}' already exists.", "AdbSync", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var device = _selected ?? new DeviceConfig { Name = name };
        device.Name = name;
        if (WifiRadio.IsChecked == true)
        {
            device.Ip = IpOrSerialBox.Text.Trim();
            device.Serial = null;
        }
        else
        {
            device.Serial = IpOrSerialBox.Text.Trim();
            device.Ip = null;
            device.CachedHostPort = null;
        }

        if (isNew)
            _config.Devices.Add(device);

        _changed = true;
        RefreshList(name);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
            return;

        var result = MessageBox.Show(
            this,
            $"Remove device '{_selected.Name}'? Any jobs referencing it will fail to connect until updated.",
            "Remove Device",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;

        _config.Devices.Remove(_selected);
        _changed = true;
        New_Click(sender, e);
        RefreshList();
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        TestResultText.Text = "Testing...";
        var device = new DeviceConfig
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "test" : NameBox.Text,
            Ip = WifiRadio.IsChecked == true ? IpOrSerialBox.Text.Trim() : null,
            Serial = UsbRadio.IsChecked == true ? IpOrSerialBox.Text.Trim() : null,
        };
        try
        {
            var resolver = new AdbDeviceResolver(new AdbClient(), new MdnsBrowser(), new AdbServer());
            var hostPort = await resolver.EnsureConnectedAsync(device);
            TestResultText.Text = $"Connected: {hostPort}";
        }
        catch (Exception ex)
        {
            TestResultText.Text = $"Failed: {ex.Message}";
        }
    }

    private void Pair_Click(object sender, RoutedEventArgs e)
    {
        var ip = IpOrSerialBox.Text.Trim();
        if (ip.Length == 0)
        {
            MessageBox.Show(this, "Enter the device's IP first.", "AdbSync", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "device" : NameBox.Text.Trim();
        var dialog = new PairDeviceWindow(name, ip) { Owner = this };
        if (dialog.ShowDialog() == true)
            TestResultText.Text = $"Paired: {dialog.PairedHostPort}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = _changed;
}
