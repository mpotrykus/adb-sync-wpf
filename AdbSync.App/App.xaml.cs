using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using AdbSync.App.Services;
using AdbSync.App.Tray;
using AdbSync.App.ViewModels;
using AdbSync.App.Views;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Models.Devices;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Models.Merge;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using AdbSync.Core.Services.Orchestration.RunHistory;
using AdbSync.Core.Models.Transfer;
using AdbSync.Core.Services.Transfer;
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
    private bool _hostStopped;

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

        var (retentionDays, maxBytesPerFile) = ReadLogSettingsOrDefault(AppPaths.Default);
        var fileLogger = AdbSyncLogging.CreateFileLogger(AppPaths.Default, retentionDays, maxBytesPerFile);

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddSerilog(fileLogger, dispose: true);
            })
            .ConfigureServices(ConfigureServices)
            .Build();
        Services = _host.Services;

        // Escape the UI thread's DispatcherSynchronizationContext for the duration of host startup: BackgroundService
        // starts its ExecuteAsync synchronously from here, so whatever context is ambient at that first `await` is
        // what every later continuation (timers, cancellation, etc.) gets posted back to. If that's the dispatcher,
        // OnExit's synchronous host-stop call below deadlocks against its own blocked thread. Task.Run guarantees a
        // thread-pool (null) context instead, so hosted-service internals never need the UI thread to make progress.
        Task.Run(() => _host.StartAsync()).GetAwaiter().GetResult();

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
    // not being built), so this reads settings.json directly - a stale/default value until next restart if the
    // user changes either setting via Settings is an acceptable tradeoff for a "polish" feature.
    private static (int RetentionDays, long MaxBytesPerFile) ReadLogSettingsOrDefault(AppPaths paths)
    {
        const int defaultDays = 30;
        const long defaultMaxBytes = 5 * 1024 * 1024;
        try
        {
            if (!File.Exists(paths.SettingsFile))
                return (defaultDays, defaultMaxBytes);

            using var doc = JsonDocument.Parse(File.ReadAllText(paths.SettingsFile));
            var retentionDays = doc.RootElement.TryGetProperty("logRetentionDays", out var retentionProp) ? retentionProp.GetInt32() : defaultDays;
            var maxBytes = doc.RootElement.TryGetProperty("perLogFileMaxBytes", out var maxBytesProp) ? maxBytesProp.GetInt64() : defaultMaxBytes;
            return (retentionDays, maxBytes);
        }
        catch (Exception ex)
        {
            // File logger isn't set up yet at this point in startup, so there's nowhere better to send this.
            Debug.WriteLine($"Failed to read log settings; using defaults ({defaultDays} days, {defaultMaxBytes} bytes). {ex}");
            return (defaultDays, defaultMaxBytes);
        }
    }

    // Stops the host without blocking the UI thread, then shuts down - unlike a bare Shutdown() call, this keeps
    // the Dispatcher message pump alive for the several seconds host.StopAsync can take, so whatever triggered the
    // exit (e.g. the tray context menu popup, a toast notification) still gets to close/render normally instead of
    // appearing to hang. Call this from UI-thread event handlers (e.g. the tray Exit menu item) instead of Shutdown().
    public async Task ExitGracefullyAsync()
    {
        if (_host is { } host)
        {
            await host.StopAsync(TimeSpan.FromSeconds(5));
            _hostStopped = true;
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Same reasoning as the Task.Run around StartAsync above: run the stop off the UI thread's
        // SynchronizationContext so hosted-service shutdown (which awaits without ConfigureAwait(false)) never
        // needs to post a continuation back to this thread, which is synchronously blocked on GetResult() below.
        // Skipped when ExitGracefullyAsync already stopped the host asynchronously ahead of this Shutdown() call.
        if (_host is { } host && !_hostStopped)
            Task.Run(() => host.StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
        _host?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }

    private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton(AppPaths.Default);
        services.AddSingleton<IAppConfigStore, AppConfigStore>();
        services.AddSingleton<AppConfigService>();
        services.AddSingleton<DashboardUiStateStore>();

        services.AddSingleton<IAdbClient>(_ => new AdbClient());
        services.AddSingleton<IAdbServer>(_ => new AdbServer());
        services.AddSingleton<IMdnsBrowser, MdnsBrowser>();
        services.AddSingleton<IAdbDeviceResolver, AdbDeviceResolver>();
        services.AddSingleton<IDeviceChangeWatcher, DeviceChangeWatcher>();
        services.AddSingleton<IAppRunningGuard, AppRunningGuard>();
        services.AddSingleton<IDevicePackageLister, AdbDevicePackageLister>();
        services.AddSingleton<ISyncLockManager, SyncLockManager>();
        services.AddSingleton<IAdbProcessRunner>(_ => new AdbProcessRunner());
        services.AddSingleton<IMirrorDiffer, MirrorDiffer>();
        services.AddSingleton<IRemoteFileSystemFactory, AdbRemoteFileSystemFactory>();
        services.AddSingleton<IAdbTransferEngine, NativeAdbTransferEngine>();
        services.AddSingleton<ITwoWayMergeEngine, TwoWayMergeEngine>();
        services.AddSingleton<IManifestStore, ManifestStore>();
        services.AddSingleton<IPushSafetyGuard, PushSafetyGuard>();
        services.AddSingleton<ICheckpointManager, CheckpointManager>();
        services.AddSingleton<IDeviceSnapshotService, DeviceSnapshotService>();
        services.AddSingleton<IRunHistoryStore, RunHistoryStore>();
        services.AddSingleton<ILiveRunLogSink, LiveRunLogSink>();

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ISyncEventSink>(sp => sp.GetRequiredService<DashboardViewModel>());
        services.AddSingleton<SyncJobRunner>();
        services.AddSingleton<JobRunService>();

        services.AddSingleton<SchedulerHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<SchedulerHostedService>());

        services.AddSingleton<ChangeWatchHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<ChangeWatchHostedService>());

        services.AddSingleton<TrayIconService>();
        services.AddTransient<DashboardWindow>();
    }
}
