using AdbSync.Core.Services.Companion;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Persistence;
using AdbSync.Engine;
using AdbSync.Engine.Services;
using AdbSync.Server;
using Serilog;

var options = ServerOptions.FromEnvironment();

if (args is ["healthcheck"])
    return await HealthCheckCommand.RunAsync(options);

var paths = AppPaths.Default;

// adb keeps its RSA key in ANDROID_USER_HOME (default ~/.android). Kept in the data volume, the server stays the same
// adb host across container rebuilds, so phones don't have to authorise it again. Set before adb is first started.
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANDROID_USER_HOME")))
{
    var adbHome = Path.Combine(paths.Root, "android");
    Directory.CreateDirectory(adbHome);
    Environment.SetEnvironmentVariable("ANDROID_USER_HOME", adbHome);
}

using var instanceLock = InstanceLock.TryAcquire(paths, "adbsync-server");
if (instanceLock is null)
{
    Console.Error.WriteLine($"The data folder '{paths.Root}' is in use by another AdbSync process.");
    return 2;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(options.ListenUrl);
builder.Host.ConfigureHostOptions(host => host.ShutdownTimeout = TimeSpan.FromSeconds(60));
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console => console.SingleLine = true);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddSerilog(AdbSyncLogging.CreateFileLogger(paths, retentionDays: 30, maxBytesPerFile: 5 * 1024 * 1024), dispose: true);

builder.Services.AddSingleton(options);
builder.Services.AddAdbSyncEngine(paths);
builder.Services.AddSingleton<ISyncEventSink>(sp => sp.GetRequiredService<JobStatusTracker>());
builder.Services.AddSingleton<IHostPowerEvents, NoHostPowerEvents>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SchedulerHostedService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChangeWatchHostedService>());

var app = builder.Build();

// docker stop sends SIGTERM: stop runs at the next file boundary instead of being killed mid-push when the grace
// period runs out.
app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<JobRunService>().CancelAllRunning());

var api = app.MapGroup($"/api/{SystemEndpoints.ApiVersion}");
api.MapSystemEndpoints();

app.Logger.LogInformation("AdbSync server listening on {Url}, data in {DataDir}", options.ListenUrl, paths.Root);
await app.RunAsync();
return 0;
