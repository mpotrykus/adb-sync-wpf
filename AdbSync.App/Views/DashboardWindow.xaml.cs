using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdbSync.App.Services;
using AdbSync.App.ViewModels;

namespace AdbSync.App.Views;

public partial class DashboardWindow : Window
{
    private readonly AppConfigService _configService;
    private readonly JobRunService _jobRunService;
    private readonly DashboardViewModel _viewModel;

    public DashboardWindow(AppConfigService configService, JobRunService jobRunService, DashboardViewModel viewModel)
    {
        InitializeComponent();
        _configService = configService;
        _jobRunService = jobRunService;
        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += async (_, _) => await RefreshAsync();
        _configService.ConfigChanged += async (_, _) => await Dispatcher.InvokeAsync(RefreshAsync);
    }

    private async Task RefreshAsync() => _viewModel.SyncFrom(await _configService.GetAsync());

    private void JobsGridBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var border = (Border)sender;
        var radius = border.CornerRadius.TopLeft;
        border.Clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Tray-resident app: hide rather than destroy, so re-opening from the tray reuses this window.
        e.Cancel = true;
        Hide();
    }

    private async void AddJob_Click(object sender, RoutedEventArgs e)
    {
        var config = await _configService.GetAsync();
        var editor = new JobEditorWindow(config, job: null) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            config.Jobs.Add(editor.Result!);
            await _configService.SaveAsync();
            await RefreshAsync();
        }
    }

    private async void EditJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        var config = await _configService.GetAsync();
        var job = config.Jobs.FirstOrDefault(j => j.Name == vm.Name);
        if (job is null)
            return;

        var editor = new JobEditorWindow(config, job) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            var index = config.Jobs.IndexOf(job);
            config.Jobs[index] = editor.Result!;
            await _configService.SaveAsync();
            await RefreshAsync();
        }
    }

    private async void RemoveJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        var confirmed = MessageBox.Show(this, $"Remove job '{vm.Name}'?", "Remove Job", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmed != MessageBoxResult.Yes)
            return;

        var config = await _configService.GetAsync();
        config.Jobs.RemoveAll(j => j.Name == vm.Name);
        await _configService.SaveAsync();
        await RefreshAsync();
    }

    private async void RunNow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobStatusViewModel vm })
            return;

        var config = await _configService.GetAsync();
        var index = config.Jobs.FindIndex(j => j.Name == vm.Name);
        if (index >= 0)
            _ = _jobRunService.RunJobAsync(index); // fire-and-forget; live status flows back via ISyncEventSink
    }

    private async void ManageDevices_Click(object sender, RoutedEventArgs e)
    {
        var config = await _configService.GetAsync();
        var window = new DeviceEditorWindow(config) { Owner = this };
        if (window.ShowDialog() == true)
            await _configService.SaveAsync();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var config = await _configService.GetAsync();
        var window = new SettingsWindow(config) { Owner = this };
        if (window.ShowDialog() == true)
        {
            await _configService.SaveAsync();
            await RefreshAsync();
        }
    }
}
