using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AdbSync.App.Services;
using AdbSync.App.ViewModels;
using AdbSync.App.Views;
using AdbSync.Core.Config;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdbSync.App.Tray;

public sealed class TrayIconService(
    AppConfigService configService,
    JobRunService jobRunService,
    SchedulerHostedService scheduler,
    DashboardViewModel dashboard,
    AppPaths paths,
    IServiceProvider services,
    ILogger<TrayIconService> logger) : IDisposable
{
    private TaskbarIcon? _icon;
    private DashboardWindow? _dashboardWindow;

    public void Initialize()
    {
        _icon = new TaskbarIcon
        {
            Icon = LoadAppIcon(),
            ToolTipText = "AdbSync: idle",
        };
        // Without an owning XAML tree, TaskbarIcon never gets its normal Loaded-triggered auto-create -
        // without this, the icon silently never appears and ShowNotification throws "TrayIcon is not created."
        _icon.ForceCreate(enablesEfficiencyMode: false);
        _icon.TrayMouseDoubleClick += (_, _) => OpenDashboard();

        foreach (var job in dashboard.Jobs)
            WireJobUpdates(job);
        dashboard.Jobs.CollectionChanged += (_, e) =>
        {
            Application.Current.Dispatcher.Invoke(RebuildContextMenu);
            if (e.NewItems is null)
                return;
            foreach (JobStatusViewModel job in e.NewItems)
                WireJobUpdates(job);
        };
        configService.ConfigChanged += (_, _) => Application.Current.Dispatcher.Invoke(RebuildContextMenu);

        RebuildContextMenu();
    }

    /// <summary>Live tray tooltip (mirrors the old tool's per-phase tooltip updates) + balloon notifications on outcome, gated by settings.</summary>
    private void WireJobUpdates(JobStatusViewModel job)
    {
        job.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(JobStatusViewModel.PhaseText))
            {
                UpdateTooltip(job);
            }
            else if (e.PropertyName == nameof(JobStatusViewModel.LastOutcome) && job.LastOutcome is not null)
            {
                await ShowOutcomeNotificationAsync(job);
            }
        };
    }

    private void UpdateTooltip(JobStatusViewModel job)
    {
        if (_icon is null)
            return;
        _icon.ToolTipText = job.PhaseText == "Idle"
            ? (dashboard.Jobs.FirstOrDefault(j => j.PhaseText != "Idle") is { } active ? $"AdbSync: [{active.Name}] {active.PhaseText}" : "AdbSync: idle")
            : $"AdbSync: [{job.Name}] {job.PhaseText}";
    }

    private async Task ShowOutcomeNotificationAsync(JobStatusViewModel job)
    {
        if (_icon is null || job.LastOutcome is null)
            return;

        try
        {
            var config = await configService.GetAsync();
            var isError = job.LastOutcome.StartsWith("Error", StringComparison.Ordinal);
            if (isError && config.Settings.ShowErrorNotifications)
                _icon.ShowNotification($"AdbSync: {job.Name}", job.LastOutcome, NotificationIcon.Error);
            else if (!isError && config.Settings.ShowInfoNotifications)
                _icon.ShowNotification($"AdbSync: {job.Name}", job.LastOutcome, NotificationIcon.Info);
        }
        catch (Exception ex)
        {
            // Toast/balloon delivery is inherently unreliable from an unpackaged exe (Focus Assist, notification
            // settings, etc.) - a failure here must never take down a tray-resident background app.
            logger.LogWarning(ex, "Failed to show notification for job '{Job}'", job.Name);
        }
    }

    private void RebuildContextMenu()
    {
        if (_icon is null)
            return;

        var menu = new ContextMenu();
        menu.Items.Add(MakeItem("Open Dashboard", (_, _) => OpenDashboard()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Run All Now", async (_, _) => await jobRunService.RunAllEnabledAsync()));

        var runJobMenu = new MenuItem { Header = "Run Job" };
        foreach (var job in dashboard.Jobs.ToList())
        {
            var name = job.Name;
            runJobMenu.Items.Add(MakeItem(name, async (_, _) =>
            {
                var config = await configService.GetAsync();
                var index = config.Jobs.FindIndex(j => j.Name == name);
                if (index >= 0)
                    await jobRunService.RunJobAsync(index);
            }));
        }
        runJobMenu.IsEnabled = runJobMenu.Items.Count > 0;
        menu.Items.Add(runJobMenu);

        menu.Items.Add(MakeItem(scheduler.Paused ? "Resume Scheduling" : "Pause Scheduling", (_, _) =>
        {
            scheduler.Paused = !scheduler.Paused;
            RebuildContextMenu();
        }));

        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Open Projects Folder", async (_, _) =>
        {
            var config = await configService.GetAsync();
            OpenFolder(config.Settings.ProjectsDirectory);
        }));
        menu.Items.Add(MakeItem("Open Logs Folder", (_, _) => OpenFolder(paths.LogsDir)));
        menu.Items.Add(MakeItem("Settings...", async (_, _) =>
        {
            var config = await configService.GetAsync();
            var window = new SettingsWindow(config) { Owner = _dashboardWindow };
            if (window.ShowDialog() == true)
                await configService.SaveAsync();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Exit", (_, _) => Application.Current.Shutdown()));

        _icon.ContextMenu = menu;
    }

    private static MenuItem MakeItem(string header, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header };
        item.Click += handler;
        return item;
    }

    private void OpenDashboard()
    {
        _dashboardWindow ??= services.GetRequiredService<DashboardWindow>();
        _dashboardWindow.Show();
        _dashboardWindow.WindowState = WindowState.Normal;
        _dashboardWindow.Activate();
    }

    private static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        var resourceInfo = Application.GetResourceStream(new Uri("Assets/app.ico", UriKind.Relative));
        using var stream = resourceInfo!.Stream;
        return new System.Drawing.Icon(stream);
    }

    public void Dispose() => _icon?.Dispose();
}
