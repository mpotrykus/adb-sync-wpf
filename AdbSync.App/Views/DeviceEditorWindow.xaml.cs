using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Devices;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using System.Windows;
using System.Windows.Controls;

namespace AdbSync.App.Views;

public partial class DeviceEditorWindow : Window
{
    private readonly AppConfig _config;
    private readonly IAdbClient _adbClient = new AdbClient();
    private DeviceConfig? _selected;
    private bool _changed;

    public bool Changed => _changed;

    private sealed record DeviceRow(string Name, bool IsConnected, bool IsPaired);

    public DeviceEditorWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        _ = RefreshListAsync();
    }

    private async Task RefreshListAsync(string? selectName = null)
    {
        IEnumerable<DeviceData> liveDevices = [];
        try
        {
            liveDevices = await _adbClient.GetDevicesAsync(CancellationToken.None);
        }
        catch
        {
        }

        bool IsConnected(DeviceConfig d) => d.Ip is not null
            ? liveDevices.Any(s => s.Serial.StartsWith($"{d.Ip}:", StringComparison.Ordinal) && s.State == DeviceState.Online)
            : d.Serial is not null && liveDevices.Any(s => s.Serial == d.Serial && s.State == DeviceState.Online);

        var rows = _config.Devices
            .Select(d => new DeviceRow(d.Name, IsConnected(d), d.CachedHostPort is not null))
            .ToList();
        DeviceList.ItemsSource = rows;
        if (selectName is not null)
            DeviceList.SelectedItem = rows.FirstOrDefault(r => r.Name == selectName);
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        NameErrorText.Visibility = Visibility.Collapsed;

        if (DeviceList.SelectedItem is not DeviceRow row)
        {
            _selected = null;
            return;
        }

        _selected = _config.Devices.FirstOrDefault(d => d.Name == row.Name);
        if (_selected is null)
            return;

        NameBox.Text = _selected.Name;
        NameBox.IsEnabled = false;
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
        NameErrorText.Visibility = Visibility.Collapsed;
        _selected = null;
        DeviceList.SelectedItem = null;
        NameBox.Text = "";
        NameBox.IsEnabled = true;
        WifiRadio.IsChecked = true;
        IpOrSerialBox.Text = "";
        TestResultText.Text = "";
    }

    private async void SaveDevice_Click(object sender, RoutedEventArgs e)
    {
        NameErrorText.Visibility = Visibility.Collapsed;

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowNameError("Name is required.");
            return;
        }

        var isNew = _selected is null;
        if (isNew && _config.Devices.Any(d => d.Name == name))
        {
            ShowNameError($"A device named '{name}' already exists.");
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
        await RefreshListAsync(name);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
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
        await RefreshListAsync();
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
            if (_selected is not null)
            {
                _selected.CachedHostPort = device.CachedHostPort;
                _selected.CachedAt = device.CachedAt;
                _changed = true;
            }
            await RefreshListAsync(_selected?.Name);
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

    private void ShowNameError(string message)
    {
        NameErrorText.Text = message;
        NameErrorText.Visibility = Visibility.Visible;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
