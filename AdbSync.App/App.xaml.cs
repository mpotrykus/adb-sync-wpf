using System.IO;
using System.Text.Json;
using System.Windows;
using AdbSync.App.Services;
using AdbSync.App.Tray;
using AdbSync.App.ViewModels;
using AdbSync.App.Views;
using AdbSync.Core.Config;
using AdbSync.Core.Devices;
using AdbSync.Core.Logging;
using AdbSync.Core.Merge;
using AdbSync.Core.Orchestration;
using AdbSync.Core.Orchestration.RunHistory;
using AdbSync.Core.Transfer;
using AdvancedSharpAdbClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AdbSync.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private Mutex? _singleInstanceMutex;

    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A tray-resident background tool must never crash to desktop unannounced - this is the last line of
        // defense for bugs in fire-and-forget "async void" event handlers (menu clicks, PropertyChanged, etc.),
        // which by default terminate the process on WPF if left unhandled.
        DispatcherUnhandledException += (_, args) =>
        {
            Services?.GetService<ILogger<App>>()?.LogError(args.Exception, "Unhandled UI exception");
            args.Handled = true;
        };

        _singleInstanceMutex = new Mutex(initiallyOwned: true, "Global\\AdbSync.App.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("AdbSync is already running.", "AdbSync", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var retentionDays = ReadLogRetentionDaysOrDefault(AppPaths.Default);
        var fileLogger = AdbSyncLogging.CreateFileLogger(AppPaths.Default, retentionDays);

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddSerilog(fileLogger, dispose: true);
            })
            .ConfigureServices(ConfigureServices)
            .Build();
        Services = _host.Services;
        _host.Start();

        Services.GetRequiredService<TrayIconService>().Initialize();

        // Force-open the dashboard at launch - useful if the tray icon is hidden, and for automated UI testing.
        if (e.Args.Contains("--dashboard"))
            Services.GetRequiredService<DashboardWindow>().Show();

        _ = ApplyStartAtLoginAsync();
    }

    // Keeps the actual registry state in sync with the persisted setting on every launch - otherwise the
    // StartAtLogin=true default never takes effect until the user happens to open Settings and click Save.
    private static async Task ApplyStartAtLoginAsync()
    {
        var config = await Services.GetRequiredService<AppConfigService>().GetAsync();
        if (StartupRegistration.IsEnabled() != config.Settings.StartAtLogin)
            StartupRegistration.SetEnabled(config.Settings.StartAtLogin);
    }

    // Config isn't loaded through AppConfigService yet at this point in startup (chicken-and-egg with the host
    // not being built), so this reads settings.json directly - a stale/default retention value until next restart
    // if the user changes it via Settings is an acceptable tradeoff for a "polish" feature.
    private static int ReadLogRetentionDaysOrDefault(AppPaths paths)
    {
        const int defaultDays = 30;
        try
        {
            if (!File.Exists(paths.SettingsFile))
                return defaultDays;

            using var doc = JsonDocument.Parse(File.ReadAllText(paths.SettingsFile));
            return doc.RootElement.TryGetProperty("logRetentionDays", out var prop) ? prop.GetInt32() : defaultDays;
        }
        catch
        {
            return defaultDays;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        _host?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }

    private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton(AppPaths.Default);
        services.AddSingleton<IAppConfigStore, AppConfigStore>();
        services.AddSingleton<AppConfigService>();

        services.AddSingleton<IAdbClient>(_ => new AdbClient());
        services.AddSingleton<IAdbServer>(_ => new AdbServer());
        services.AddSingleton<IMdnsBrowser, MdnsBrowser>();
        services.AddSingleton<IAdbDeviceResolver, AdbDeviceResolver>();
        services.AddSingleton<IAppRunningGuard, AppRunningGuard>();
        services.AddSingleton<ISyncLockManager, SyncLockManager>();
        services.AddSingleton<IAdbProcessRunner>(_ => new AdbProcessRunner());
        services.AddSingleton<IMirrorDiffer, MirrorDiffer>();
        services.AddSingleton<IRemoteFileSystemFactory, AdbRemoteFileSystemFactory>();
        services.AddSingleton<IAdbTransferEngine, NativeAdbTransferEngine>();
        services.AddSingleton<ITwoWayMergeEngine, TwoWayMergeEngine>();
        services.AddSingleton<IManifestStore, ManifestStore>();
        services.AddSingleton<IPushSafetyGuard, PushSafetyGuard>();
        services.AddSingleton<ICheckpointManager, CheckpointManager>();
        services.AddSingleton<IRunHistoryStore, RunHistoryStore>();

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ISyncEventSink>(sp => sp.GetRequiredService<DashboardViewModel>());
        services.AddSingleton<SyncJobRunner>();
        services.AddSingleton<JobRunService>();

        services.AddSingleton<SchedulerHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<SchedulerHostedService>());

        services.AddSingleton<TrayIconService>();
        services.AddTransient<DashboardWindow>();
    }
}
