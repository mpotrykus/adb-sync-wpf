using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AdbSync.App.Services;
using AdbSync.App.ViewModels;
using AdbSync.App.Views;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
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

    private System.Drawing.Icon _appIcon = null!;
    private System.Drawing.Icon _pullIcon = null!;
    private System.Drawing.Icon _pushIcon = null!;
    private System.Drawing.Icon _mergeIcon = null!;
    private System.Drawing.Icon _warningIcon = null!;

    public void Initialize()
    {
        _appIcon = LoadIcon("app.ico");
        _pullIcon = LoadIcon("pull.ico");
        _pushIcon = LoadIcon("push.ico");
        _mergeIcon = LoadIcon("merge.ico");
        _warningIcon = LoadIcon("warning.ico");

        _icon = new TaskbarIcon
        {
            Icon = (System.Drawing.Icon)_appIcon.Clone(),
            ToolTipText = "AdbSync: idle",
        };

        _icon.ForceCreate(enablesEfficiencyMode: false);
        _icon.TrayMouseDoubleClick += (_, _) => OpenDashboard();

        foreach (var job in dashboard.Jobs)
            WireJobUpdates(job);
        dashboard.Jobs.CollectionChanged += (_, e) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                RebuildContextMenu();
                UpdateTrayIcon();
            });
            if (e.NewItems is null)
                return;
            foreach (JobStatusViewModel job in e.NewItems)
                WireJobUpdates(job);
        };
        configService.ConfigChanged += (_, _) => Application.Current.Dispatcher.Invoke(RebuildContextMenu);

        RebuildContextMenu();
        UpdateTrayIcon();
    }

    /// <summary>Live tray tooltip (mirrors the old tool's per-phase tooltip updates) + balloon notifications on outcome, gated by settings.</summary>
    private void WireJobUpdates(JobStatusViewModel job)
    {
        job.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobStatusViewModel.PhaseText))
                UpdateTooltip(job);
            if (e.PropertyName is nameof(JobStatusViewModel.PhaseText) or nameof(JobStatusViewModel.NeedsAttention))
                UpdateTrayIcon();
        };
        job.OutcomeReported += async _ => await ShowOutcomeNotificationAsync(job);
    }

    private void UpdateTooltip(JobStatusViewModel job)
    {
        if (_icon is null)
            return;
        _icon.ToolTipText = job.PhaseText == "Idle"
            ? (dashboard.Jobs.FirstOrDefault(j => j.PhaseText != "Idle") is { } active ? $"AdbSync: [{active.Name}] {active.PhaseText}" : "AdbSync: idle")
            : $"AdbSync: [{job.Name}] {job.PhaseText}";
    }

    /// <summary>Picks the tray glyph from current job states: a job needing attention (force-push required,
    /// device unreachable, etc.) always wins over an in-progress phase, otherwise the busiest active phase
    /// across all jobs is shown, falling back to the plain app icon when everything is idle/watching.</summary>
    private void UpdateTrayIcon()
    {
        if (_icon is null)
            return;

        var jobs = dashboard.Jobs;
        var master = jobs.Any(j => j.NeedsAttention) ? _warningIcon
            : jobs.Any(j => j.PhaseText.StartsWith("Push", StringComparison.Ordinal)) ? _pushIcon
            : jobs.Any(j => j.PhaseText.StartsWith("Pull", StringComparison.Ordinal)) ? _pullIcon
            : jobs.Any(j => j.PhaseText.StartsWith("Merge", StringComparison.Ordinal)) ? _mergeIcon
            : _appIcon;

        // TaskbarIcon.Icon disposes whatever the *previous* value was as soon as a new one is assigned, so the
        // cached master icons above must never be handed to it directly - only a throwaway clone each time,
        // or the second reuse of a master (e.g. pull.ico on device #2) throws ObjectDisposedException mid-run.
        _icon.Icon = (System.Drawing.Icon)master.Clone();
    }

    private async Task ShowOutcomeNotificationAsync(JobStatusViewModel job)
    {
        if (_icon is null || job.LastOutcome is null)
            return;

        try
        {
            var config = await configService.GetAsync();
            if (config.Settings.IsQuietNow())
                return;

            var jobConfig = config.Jobs.FirstOrDefault(j => j.Name == job.Name);
            var eff = jobConfig?.Resolve(config.Settings);
            var showInfo = eff?.ShowInfoNotifications ?? config.Settings.ShowInfoNotifications;
            var showErrors = eff?.ShowErrorNotifications ?? config.Settings.ShowErrorNotifications;

            var isError = job.LastOutcome.StartsWith("Error", StringComparison.Ordinal);
            if (isError && showErrors)
                _icon.ShowNotification($"AdbSync: {job.Name}", job.LastOutcome, NotificationIcon.Error);
            else if (!isError && showInfo)
                _icon.ShowNotification($"AdbSync: {job.Name}", job.LastOutcome, NotificationIcon.Info);
        }
        catch (Exception ex)
        {
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
            _dashboardWindow ??= services.GetRequiredService<DashboardWindow>();
            await _dashboardWindow.OpenSettingsAsync();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Exit", async (_, _) =>
        {
            _icon?.Dispose();
            _icon = null;

            if (Application.Current is App app)
                await app.ExitGracefullyAsync();
        }));

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

    private static System.Drawing.Icon LoadIcon(string fileName)
    {
        var resourceInfo = Application.GetResourceStream(new Uri($"Assets/{fileName}", UriKind.Relative));
        using var stream = resourceInfo!.Stream;
        return new System.Drawing.Icon(stream);
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _appIcon?.Dispose();
        _pullIcon?.Dispose();
        _pushIcon?.Dispose();
        _mergeIcon?.Dispose();
        _warningIcon?.Dispose();
    }
}
