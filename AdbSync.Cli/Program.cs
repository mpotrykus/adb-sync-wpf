using AdbSync.Cli;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Config.Legacy;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Orchestration.RunHistory;
using AdbSync.Core.Services.Transfer;
using AdvancedSharpAdbClient;
using Microsoft.Extensions.Logging;
using Serilog;

var paths = AppPaths.Default;
var configStore = new AppConfigStore(paths);

return args switch
{
    ["config", "import", var devicesPath, var projectsPath] => await ImportConfigAsync(devicesPath, projectsPath),
    ["device", "test", var deviceName] => await TestDeviceAsync(deviceName),
    ["device", "pair", var deviceName, var code] => await PairDeviceAsync(deviceName, code),
    ["run", "--legacy-transfer"] => await RunAllAsync(useNativeTransfer: false),
    ["run"] => await RunAllAsync(useNativeTransfer: true),
    ["run", var jobName, "--legacy-transfer"] => await RunOneAsync(jobName, useNativeTransfer: false),
    ["run", var jobName] => await RunOneAsync(jobName, useNativeTransfer: true),
    ["run", var jobName, "--force-push"] => await RunOneAsync(jobName, useNativeTransfer: true, forcePush: true),
    _ => PrintUsage(),
};

async Task<int> ImportConfigAsync(string legacyDevicesPath, string legacyProjectsPath)
{
    var importer = new LegacyConfigImporter();
    var imported = await importer.ImportAsync(legacyDevicesPath, legacyProjectsPath);
    await configStore.SaveAsync(imported);
    Console.WriteLine($"Imported {imported.Devices.Count} device(s) and {imported.Jobs.Count} job(s) into {paths.ConfigDir}");
    return 0;
}

async Task<int> TestDeviceAsync(string deviceName)
{
    var config = await configStore.LoadAsync();
    var device = config.Devices.FirstOrDefault(d => d.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
    if (device is null)
    {
        Console.Error.WriteLine($"No device named '{deviceName}' in {paths.DevicesFile}");
        return 1;
    }

    var resolver = new AdbDeviceResolver(new AdbClient(), new MdnsBrowser(), new AdbServer(), logger: CreateConsoleLogger<AdbDeviceResolver>());
    try
    {
        var hostPort = await resolver.EnsureConnectedAsync(device);
        Console.WriteLine($"Connected: {deviceName} -> {hostPort}");
        await configStore.SaveAsync(config);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to connect to '{deviceName}': {ex.Message}");
        return 1;
    }
}

async Task<int> PairDeviceAsync(string deviceName, string code)
{
    var config = await configStore.LoadAsync();
    var device = config.Devices.FirstOrDefault(d => d.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
    if (device is null)
    {
        Console.Error.WriteLine($"No device named '{deviceName}' in {paths.DevicesFile}");
        return 1;
    }

    var resolver = new AdbDeviceResolver(new AdbClient(), new MdnsBrowser(), new AdbServer(), logger: CreateConsoleLogger<AdbDeviceResolver>());
    try
    {
        var hostPort = await resolver.PairAsync(device, code);
        Console.WriteLine($"Paired: {deviceName} -> {hostPort}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to pair with '{deviceName}': {ex.Message}");
        return 1;
    }
}

async Task<int> RunAllAsync(bool useNativeTransfer)
{
    var config = await configStore.LoadAsync();
    var checkpoints = new CheckpointManager(paths);

    var orchestrator = new SyncOrchestrator(BuildRunner(useNativeTransfer), checkpoints);
    var results = await orchestrator.RunAllAsync(config);

    await configStore.SaveAsync(config);
    return PrintResultsAndExitCode(config, results);
}

async Task<int> RunOneAsync(string jobName, bool useNativeTransfer, bool forcePush = false)
{
    var config = await configStore.LoadAsync();
    var index = config.Jobs.FindIndex(j => j.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
    if (index < 0)
    {
        Console.Error.WriteLine($"No job named '{jobName}' in {paths.ProjectsFile}");
        return 1;
    }

    var checkpoints = new CheckpointManager(paths);
    var resume = await checkpoints.LoadAsync(config.Jobs[index].Name);

    var runner = BuildRunner(useNativeTransfer);
    var result = await runner.RunAsync(config.Jobs[index], index, config.Devices, config.Settings, resume, forcePush);

    await configStore.SaveAsync(config);
    return PrintResultsAndExitCode(config, [result], [jobName]);
}

ILogger<T> CreateConsoleLogger<T>() =>
    LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug)).CreateLogger<T>();

SyncJobRunner BuildRunner(bool useNativeTransfer)
{
    var adbClient = new AdbClient();
    IAdbTransferEngine transfer = useNativeTransfer
        ? new NativeAdbTransferEngine(new AdbRemoteFileSystemFactory(adbClient), new MirrorDiffer())
        : new AdbExeTransferEngine(new AdbProcessRunner(), new MirrorDiffer());

    var loggerFactory = LoggerFactory.Create(b => b.AddSerilog(
        AdbSyncLogging.CreateFileLogger(paths, retentionDays: 30, maxBytesPerFile: 5 * 1024 * 1024), dispose: true));

    return new SyncJobRunner(
        new AdbDeviceResolver(adbClient, new MdnsBrowser(), new AdbServer(), logger: loggerFactory.CreateLogger<AdbDeviceResolver>()),
        new AppRunningGuard(adbClient),
        new SyncLockManager(),
        transfer,
        new TwoWayMergeEngine(),
        new ManifestStore(paths),
        new PushSafetyGuard(paths),
        new CheckpointManager(paths),
        new ConsoleSyncEventSink(),
        new RunHistoryStore(paths),
        loggerFactory.CreateLogger<SyncJobRunner>());
}

int PrintResultsAndExitCode(AppConfig config, IReadOnlyList<JobRunResult> results, IReadOnlyList<string>? jobNames = null)
{
    var names = jobNames ?? config.Jobs.Where(j => j.Enabled).Select(j => j.Name).ToList();
    for (var i = 0; i < results.Count; i++)
    {
        var name = i < names.Count ? names[i] : $"job[{i}]";
        var result = results[i];
        Console.WriteLine(result.ErrorMessage is null
            ? $"{name}: {result.Outcome}"
            : $"{name}: {result.Outcome} - {result.ErrorMessage}");
    }
    return results.Any(r => r.Outcome == JobRunOutcome.Failed) ? 1 : 0;
}

int PrintUsage()
{
    Console.WriteLine("""
        Usage:
          adbsync config import <legacyDevices.json> <legacyProjects.json>
          adbsync device test <deviceName>
          adbsync device pair <deviceName> <code>  # one-time wireless-debugging pairing; device must have 'Pair device with pairing code' open
          adbsync run                    # run every enabled job (native ADB sync protocol), resuming an interrupted run if one exists
          adbsync run --legacy-transfer  # same, but via the v1 adb.exe-shelling transfer engine
          adbsync run <jobName>          # run a single job by name (ignores any pending checkpoint)
          adbsync run <jobName> --legacy-transfer
          adbsync run <jobName> --force-push  # bypass the push-safety check and rebase its baseline to the current file count
        """);
    return 1;
}
