using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Companion;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Persistence;
using AdbSync.Core.Services.Transfer;
using AdbSync.Engine;
using AdbSync.Engine.Services;
using AdbSync.Server;
using AdbSync.Server.Api;
using AdbSync.Server.Auth;
using AdbSync.Server.Events;
using AdvancedSharpAdbClient;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;
using System.Threading.RateLimiting;

var options = ServerOptions.FromEnvironment();

if (args is ["healthcheck"])
    return await HealthCheckCommand.RunAsync(options);

var paths = AppPaths.Default;

if (args is ["pair"])
    return await PairCommand.RunAsync(paths, options);
if (args.Length > 0)
{
    Console.Error.WriteLine("Usage: adbsync-server [pair | healthcheck]");
    return 1;
}

// Debian's adb 34 ignores ANDROID_USER_HOME, ANDROID_SDK_HOME and ANDROID_PREFS_ROOT and always keeps its RSA key in
// $HOME/.android, which in a container is lost on every recreate and leaves every phone distrusting the server. Pointing
// HOME into the data volume keeps the key, so phones only have to authorise this server once. Set before adb first starts.
if (!OperatingSystem.IsWindows())
{
    var adbHome = Path.Combine(paths.Root, "adb-home");
    Directory.CreateDirectory(adbHome);
    Environment.SetEnvironmentVariable("HOME", adbHome);
}

using var instanceLock = InstanceLock.TryAcquire(paths, "adbsync-server");
if (instanceLock is null)
{
    Console.Error.WriteLine($"The data folder '{paths.Root}' is in use by another AdbSync process.");
    return 2;
}

var identity = new ServerIdentity(paths, options);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Listen(options.ListenAddress, options.Port, listen => listen.UseHttps(identity.Certificate)));
builder.Host.ConfigureHostOptions(host => host.ShutdownTimeout = TimeSpan.FromSeconds(60));
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console => console.SingleLine = true);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddSerilog(AdbSyncLogging.CreateFileLogger(paths, retentionDays: 30, maxBytesPerFile: 5 * 1024 * 1024), dispose: true);

var features = builder.Configuration.GetSection(FeatureFlags.SectionName).Get<FeatureFlags>() ?? new FeatureFlags();
builder.Services.AddSingleton(features);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AuthStore>();
builder.Services.AddSingleton<EventBus>();
builder.Services.AddSingleton<JobStatusBuilder>();
builder.Services.AddSingleton<RunRegistry>();
builder.Services.AddSingleton<ConfigEditor>();
builder.Services.AddSingleton<OperationRegistry>();
builder.Services.AddSingleton<WatchStateTracker>();
builder.Services.AddSingleton<AppIconService>();
builder.Services.AddSingleton<JobStateRenamer>();
builder.Services.AddHostedService<EngineEventSource>();
builder.Services.AddAdbSyncEngine(paths);
if (!features.DeviceWrites)
{
    var writeDelay = TimeSpan.FromSeconds(Math.Max(0, features.SimulatedWriteDelaySeconds));
    builder.Services.AddSingleton(sp => new DeviceWriteSimulator(writeDelay, sp.GetRequiredService<ILogger<DeviceWriteSimulator>>()));
    builder.Services.AddSingleton<IRemoteFileSystemFactory>(sp => new SimulatedWriteRemoteFileSystemFactory(
        new AdbRemoteFileSystemFactory(sp.GetRequiredService<IAdbClient>()), sp.GetRequiredService<DeviceWriteSimulator>()));
    builder.Services.AddSingleton<IDeviceAppControl, SimulatedDeviceAppControl>();
}
builder.Services.AddSingleton<LiveRunLogSink>();
builder.Services.AddSingleton<ILiveRunLogSink>(sp => new PublishingRunLogSink(
    sp.GetRequiredService<LiveRunLogSink>(), sp.GetRequiredService<EventBus>(), sp.GetRequiredService<RunRegistry>()));
builder.Services.AddSingleton<ISyncEventSink>(sp => new CompositeSyncEventSink(
[
    sp.GetRequiredService<JobStatusTracker>(),
    sp.GetRequiredService<RunRegistry>(),
    sp.GetRequiredService<WatchStateTracker>(),
    sp.GetRequiredService<CompanionBroadcastService>().Sink,
]));
builder.Services.AddSingleton<IHostPowerEvents, NoHostPowerEvents>();
if (features.CompanionBroadcast)
    builder.Services.AddHostedService(sp => sp.GetRequiredService<CompanionBroadcastService>());
if (features.Scheduler)
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SchedulerHostedService>());
if (features.ChangeWatch)
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ChangeWatchHostedService>());
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(limiter =>
{
    limiter.AddPolicy(AuthEndpoints.PairRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    limiter.OnRejected = async (context, ct) =>
        await Problems.Create(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many pairing attempts. Wait a minute and try again.")
            .ExecuteAsync(context.HttpContext);
});

var app = builder.Build();
app.UseBlazorFrameworkFiles();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.Services.GetRequiredService<RunRegistry>().Attach(app.Services.GetRequiredService<JobRunService>());
if (!features.RunJobs)
    app.Services.GetRequiredService<JobRunService>().HoldNewRuns("running jobs is turned off on this server (Features:RunJobs)");

// docker stop sends SIGTERM: stop runs at the next file boundary instead of being killed mid-push when the grace
// period runs out.
app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<JobRunService>().CancelAllRunning());

var api = app.MapGroup($"/api/{SystemEndpoints.ApiVersion}").RequireClientToken();
api.MapSystemEndpoints();
api.MapAuthEndpoints();
api.MapStatusEndpoints();
api.MapEventEndpoints();
api.MapJobEndpoints();
api.MapRunEndpoints();
api.MapDeviceEndpoints();
api.MapConflictEndpoints();
api.MapSettingsEndpoints();
api.MapJobExtraEndpoints();

// The web UI's client-side routes. Unknown /api paths still get the API's 404, not the app's index page.
app.MapFallbackToFile("{*path:regex(^(?!api/).*$):nonfile}", "index.html");

if (features is not { Scheduler: true, ChangeWatch: true, CompanionBroadcast: true, RunJobs: true, DeviceWrites: true })
    app.Logger.LogWarning(
        "Feature flags: Scheduler={Scheduler} ChangeWatch={ChangeWatch} CompanionBroadcast={CompanionBroadcast} RunJobs={RunJobs} DeviceWrites={DeviceWrites} (simulated write delay {Delay}s)",
        features.Scheduler, features.ChangeWatch, features.CompanionBroadcast, features.RunJobs, features.DeviceWrites, features.SimulatedWriteDelaySeconds);

if (app.Environment.IsDevelopment())
    await DevProjectsDirectory.EnsureLocalAsync(app.Services.GetRequiredService<AppConfigService>(), paths, app.Logger);
app.Logger.LogInformation(
    "AdbSync server listening on https://{Address}:{Port} as {Url}, data in {DataDir}, certificate fingerprint {Fingerprint}",
    options.ListenAddress, options.Port, identity.PublicUrl, paths.Root, identity.Fingerprint);
await app.RunAsync();
return 0;
