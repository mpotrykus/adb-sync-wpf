using AdbSync.Core.Services.Companion;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Orchestration.RunHistory;
using AdbSync.Core.Services.Persistence;
using AdbSync.Core.Services.Transfer;
using AdbSync.Engine.Services;
using AdvancedSharpAdbClient;
using Microsoft.Extensions.DependencyInjection;

namespace AdbSync.Engine;

public static class EngineServiceCollectionExtensions
{
    /// <summary>
    /// Everything the sync engine needs, shared by the desktop app and the server. Each host still registers its own
    /// <see cref="ISyncEventSink"/>, <see cref="IHostPowerEvents"/> and whichever hosted services it runs.
    /// </summary>
    public static IServiceCollection AddAdbSyncEngine(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton(sp => new EngineDatabase(sp.GetRequiredService<AppPaths>()));
        services.AddSingleton<IAppConfigStore>(sp => new AppConfigStore(sp.GetRequiredService<EngineDatabase>(), sp.GetRequiredService<AppPaths>()));
        services.AddSingleton<AppConfigService>();
        services.AddSingleton<SchedulerStateStore>();
        services.AddSingleton<DashboardUiStateStore>();

        services.AddSingleton<IAdbClient>(_ => new AdbClient());
        services.AddSingleton<IAdbServer>(_ => new AdbServer());
        services.AddSingleton<IMdnsBrowser, MdnsBrowser>();
        services.AddSingleton<IAdbDeviceResolver, AdbDeviceResolver>();
        services.AddSingleton<IDeviceChangeWatcher, DeviceChangeWatcher>();
        services.AddSingleton<IAppRunningGuard, AppRunningGuard>();
        services.AddSingleton<IDeviceAppControl, DeviceAppControl>();
        services.AddSingleton<IDevicePackageLister, AdbDevicePackageLister>();
        services.AddSingleton<ISyncLockManager, SyncLockManager>();
        services.AddSingleton<IDeviceAccessGate, DeviceAccessGate>();
        services.AddSingleton<IAdbProcessRunner>(_ => new AdbProcessRunner());
        services.AddSingleton<IMirrorDiffer, MirrorDiffer>();
        services.AddSingleton<IRemoteFileSystemFactory, AdbRemoteFileSystemFactory>();
        services.AddSingleton<IAdbTransferEngine, NativeAdbTransferEngine>();
        services.AddSingleton<ITwoWayMergeEngine, TwoWayMergeEngine>();
        services.AddSingleton<IManifestStore>(sp => new ManifestStore(sp.GetRequiredService<EngineDatabase>()));
        services.AddSingleton<IPendingConflictStore>(sp => new PendingConflictStore(sp.GetRequiredService<EngineDatabase>(), sp.GetRequiredService<AppPaths>()));
        services.AddSingleton<IConflictResolver, ConflictResolver>();
        services.AddSingleton<IPushSafetyGuard>(sp => new PushSafetyGuard(sp.GetRequiredService<EngineDatabase>()));
        services.AddSingleton<ICheckpointManager>(sp => new CheckpointManager(sp.GetRequiredService<EngineDatabase>()));
        services.AddSingleton<IDeviceSnapshotService, DeviceSnapshotService>();
        services.AddSingleton<RollbackPointStore>();
        services.AddSingleton<RollbackService>();
        services.AddSingleton<IRunHistoryStore, RunHistoryStore>();
        services.AddSingleton<ILiveRunLogSink, LiveRunLogSink>();
        services.AddSingleton<ILiveRunStepSink, LiveRunStepSink>();

        services.AddSingleton<CompanionCertificateStore>();
        services.AddSingleton<CompanionBroadcastService>();
        services.AddSingleton<JobStatusTracker>();
        services.AddSingleton<SyncJobRunner>();
        services.AddSingleton<JobRunService>();
        services.AddSingleton<AppGateService>();
        services.AddSingleton<SchedulerHostedService>();
        services.AddSingleton<ChangeWatchHostedService>();
        return services;
    }
}
