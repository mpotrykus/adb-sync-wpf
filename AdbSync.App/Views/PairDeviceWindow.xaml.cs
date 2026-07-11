using System.Windows;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Models.Devices;
using AdbSync.Core.Services.Devices;
using AdvancedSharpAdbClient;

namespace AdbSync.App.Views;

public partial class PairDeviceWindow : Window
{
    private readonly DeviceConfig _device;

    public string? PairedHostPort { get; private set; }

    public PairDeviceWindow(string deviceName, string ip)
    {
        InitializeComponent();
        _device = new DeviceConfig { Name = deviceName, Ip = ip };
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        if (code.Length == 0)
        {
            ResultText.Text = "Enter the pairing code shown on the device.";
            return;
        }

        PairButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        ResultText.Text = "Pairing...";
        try
        {
            var resolver = new AdbDeviceResolver(new AdbClient(), new MdnsBrowser(), new AdbServer());
            PairedHostPort = await resolver.PairAsync(_device, code);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ResultText.Text = $"Failed: {ex.Message}";
            PairButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
