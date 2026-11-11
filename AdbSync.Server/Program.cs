using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Companion;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Persistence;
using AdbSync.Engine;
using AdbSync.Engine.Services;
using AdbSync.Server;
using AdbSync.Server.Api;
using AdbSync.Server.Auth;
using AdbSync.Server.Events;
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

var identity = new ServerIdentity(paths, options);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Listen(options.ListenAddress, options.Port, listen => listen.UseHttps(identity.Certificate)));
builder.Host.ConfigureHostOptions(host => host.ShutdownTimeout = TimeSpan.FromSeconds(60));
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console => console.SingleLine = true);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddSerilog(AdbSyncLogging.CreateFileLogger(paths, retentionDays: 30, maxBytesPerFile: 5 * 1024 * 1024), dispose: true);

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
builder.Services.AddSingleton<JobStateRenamer>();
builder.Services.AddHostedService<EngineEventSource>();
builder.Services.AddAdbSyncEngine(paths);
builder.Services.AddSingleton<LiveRunLogSink>();
builder.Services.AddSingleton<ILiveRunLogSink>(sp => new PublishingRunLogSink(
    sp.GetRequiredService<LiveRunLogSink>(), sp.GetRequiredService<EventBus>(), sp.GetRequiredService<RunRegistry>()));
builder.Services.AddSingleton<ISyncEventSink>(sp => new CompositeSyncEventSink(
[
    sp.GetRequiredService<JobStatusTracker>(),
    sp.GetRequiredService<RunRegistry>(),
    sp.GetRequiredService<WatchStateTracker>(),
]));
builder.Services.AddSingleton<IHostPowerEvents, NoHostPowerEvents>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SchedulerHostedService>());
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

app.Logger.LogInformation(
    "AdbSync server listening on https://{Address}:{Port} as {Url}, data in {DataDir}, certificate fingerprint {Fingerprint}",
    options.ListenAddress, options.Port, identity.PublicUrl, paths.Root, identity.Fingerprint);
await app.RunAsync();
return 0;
