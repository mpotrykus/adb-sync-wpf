using AdbSync.App.Services;
using AdbSync.App.Tray;
using AdbSync.App.ViewModels;
using AdbSync.App.Views;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Orchestration.RunHistory;
using AdbSync.Core.Services.Transfer;
using AdvancedSharpAdbClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

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

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SetCurrentProcessExplicitAppUserModelID("AdbSync");

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
        Task.Run(() => _host.StartAsync()).GetAwaiter().GetResult();

        Services.GetRequiredService<TrayIconService>().Initialize();

        if (e.Args.Contains("--dashboard"))
            Services.GetRequiredService<DashboardWindow>().Show();

        _ = ApplyStartAtLoginAsync();
    }

    private static async Task ApplyStartAtLoginAsync()
    {
        var config = await Services.GetRequiredService<AppConfigService>().GetAsync();
        if (StartupRegistration.IsEnabled() != config.Settings.StartAtLogin)
            StartupRegistration.SetEnabled(config.Settings.StartAtLogin);
    }

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
            Debug.WriteLine($"Failed to read log settings; using defaults ({defaultDays} days, {defaultMaxBytes} bytes). {ex}");
            return (defaultDays, defaultMaxBytes);
        }
    }

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
        services.AddSingleton<IDeviceAccessGate, DeviceAccessGate>();
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
