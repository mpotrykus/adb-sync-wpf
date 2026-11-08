using AdbSync.Core.Services.Companion;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Orchestration.RunHistory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AdbSync.App.Services;

public sealed record CompanionJobDto(
    string Name,
    bool Enabled,
    bool Running,
    string? Phase,
    IReadOnlyList<DevicePhase> Devices,
    string? LastResult,
    bool LastFailed,
    DateTimeOffset? LastResultAt);

public sealed record CompanionStatusDto(string Pc, IReadOnlyList<CompanionJobDto> Jobs);

public sealed record CompanionEndpointReport(int Port);

public sealed record CompanionPairRequest(int Port, string Code);

/// <summary>LAN HTTPS feed for the companion app: a status snapshot, an SSE stream of job changes, the job's log
/// tail, and run/stop actions. Every request needs the bearer token from <c>GlobalSettings.CompanionServerToken</c>.</summary>
public sealed class CompanionServerService(
    AppConfigService configService,
    JobStatusTracker tracker,
    JobRunService jobRunService,
    ChangeWatchHostedService changeWatch,
    IRunHistoryStore runHistory,
    ILiveRunLogSink liveLog,
    CompanionCertificateStore certificates,
    IAdbDeviceResolver deviceResolver,
    ILogger<CompanionServerService> logger) : IHostedService
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int LogTailLines = 200;

    private WebApplication? _app;
    private readonly CancellationTokenSource _stopping = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = await configService.GetAsync();
        if (!config.Settings.CompanionServerEnabled)
            return;

        if (string.IsNullOrEmpty(config.Settings.CompanionServerToken))
        {
            config.Settings.CompanionServerToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            await configService.SaveAsync();
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var certificate = certificates.Certificate;
        builder.WebHost.ConfigureKestrel(k =>
            k.Listen(IPAddress.Any, config.Settings.CompanionServerPort, listen => listen.UseHttps(certificate)));

        var app = builder.Build();
        var tokenBytes = Encoding.UTF8.GetBytes(config.Settings.CompanionServerToken);
        app.Use(async (ctx, next) =>
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            var supplied = header.StartsWith("Bearer ", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(header[7..]) : [];
            if (!CryptographicOperations.FixedTimeEquals(supplied, tokenBytes))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(ctx);
        });

        app.MapGet("/api/status", async () => Results.Json(await BuildStatusAsync(), Json));
        app.MapGet("/api/events", StreamEventsAsync);
        app.MapGet("/api/jobs/{name}/log", GetLogAsync);
        app.MapPost("/api/jobs/{name}/run", RunJobAsync);
        app.MapPost("/api/jobs/{name}/stop", StopJobAsync);
        app.MapPost("/api/devices/{name}/endpoint", ReportEndpointAsync);
        app.MapPost("/api/devices/{name}/pair", PairDeviceAsync);

        try
        {
            await app.StartAsync(cancellationToken);
            _app = app;
            logger.LogInformation("Companion server listening on port {Port}", config.Settings.CompanionServerPort);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Companion server failed to start on port {Port}", config.Settings.CompanionServerPort);
            await app.DisposeAsync();
            return;
        }

        _ = Task.Run(() => SeedFromHistoryAsync(config.Jobs.Select(j => j.Name).ToList()), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        if (_app is { } app)
        {
            await app.StopAsync(cancellationToken);
            await app.DisposeAsync();
            _app = null;
        }
    }

    private async Task SeedFromHistoryAsync(IReadOnlyList<string> jobNames)
    {
        foreach (var name in jobNames)
        {
            try
            {
                var latest = (await runHistory.ListRunsAsync(name, _stopping.Token)).FirstOrDefault();
                if (latest is not null)
                    tracker.SeedFromHistory(latest);
            }
            catch (Exception ex) when (!_stopping.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Couldn't seed companion status for '{Job}' from run history", name);
            }
        }
    }

    private async Task<CompanionStatusDto> BuildStatusAsync()
    {
        var config = await configService.GetAsync();
        var jobs = config.Jobs.Select(j => ToDto(tracker.Get(j.Name), j.Enabled)).ToList();
        return new CompanionStatusDto(Environment.MachineName, jobs);
    }

    private static CompanionJobDto ToDto(JobStatus s, bool enabled) =>
        new(s.Name, enabled, s.Running, s.Phase, s.Devices, s.LastResult, s.LastFailed, s.LastResultAt);

    private async Task StreamEventsAsync(HttpContext ctx)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, _stopping.Token);
        var ct = linked.Token;

        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";

        var changes = Channel.CreateBounded<JobStatus>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        void OnChanged(JobStatus s) => changes.Writer.TryWrite(s);
        tracker.Changed += OnChanged;
        try
        {
            await WriteEventAsync(ctx, "snapshot", await BuildStatusAsync(), ct);
            while (!ct.IsCancellationRequested)
            {
                var wait = changes.Reader.WaitToReadAsync(ct).AsTask();
                if (await Task.WhenAny(wait, Task.Delay(PingInterval, ct)) != wait)
                {
                    await ctx.Response.WriteAsync(": ping\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    continue;
                }

                if (!await wait)
                    break;

                var config = await configService.GetAsync();
                while (changes.Reader.TryRead(out var s))
                {
                    var enabled = config.Jobs.FirstOrDefault(j => j.Name == s.Name)?.Enabled ?? false;
                    await WriteEventAsync(ctx, "job", ToDto(s, enabled), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            tracker.Changed -= OnChanged;
        }
    }

    private static async Task WriteEventAsync<T>(HttpContext ctx, string name, T payload, CancellationToken ct)
    {
        await ctx.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(payload, Json)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    private async Task<IResult> GetLogAsync(string name)
    {
        string? text = null;
        if (tracker.Get(name).Running && liveLog.TryGet(name, out _, out var live))
        {
            text = live;
        }
        else
        {
            var latest = (await runHistory.ListRunsAsync(name)).FirstOrDefault();
            if (latest is not null)
                text = await runHistory.GetRunLogAsync(name, latest.RunId);
        }

        if (text is null)
            return Results.NotFound();

        var lines = text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        return Results.Text(string.Join('\n', lines.TakeLast(LogTailLines)), "text/plain", Encoding.UTF8);
    }

    private async Task<IResult> RunJobAsync(string name)
    {
        var config = await configService.GetAsync();
        var index = config.Jobs.FindIndex(j => j.Name == name);
        if (index < 0)
            return Results.NotFound();

        logger.LogInformation("Companion app requested a run of '{Job}'", name);
        _ = Task.Run(async () =>
        {
            try
            {
                await jobRunService.RunJobAsync(index);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Companion-requested run of '{Job}' failed to start", name);
            }
        });
        return Results.Accepted();
    }

    /// <summary>The phone's wireless-debugging port after it (re)enabled wireless debugging. The IP comes from the
    /// connection itself rather than the request body, so a report can only ever point at the phone that sent it.</summary>
    private async Task<IResult> ReportEndpointAsync(string name, CompanionEndpointReport report, HttpContext ctx)
    {
        if (report.Port is < 1 or > 65535)
            return Results.BadRequest("port out of range");

        var config = await configService.GetAsync();
        var device = config.Devices.FirstOrDefault(d => d.Name == name);
        if (device is null)
            return Results.NotFound();
        if (device.Ip is null || ctx.Connection.RemoteIpAddress is not { } remote)
            return Results.BadRequest("not a wireless device");

        var ip = remote.MapToIPv4().ToString();
        var hostPort = $"{ip}:{report.Port}";
        if (device.CachedHostPort == hostPort && device.Ip == ip)
            return Results.NoContent();

        if (device.Ip != ip)
            logger.LogInformation("Device '{Device}' reported a new IP {Ip} (was {OldIp})", name, ip, device.Ip);
        logger.LogInformation("Device '{Device}' reported wireless debugging at {HostPort}", name, hostPort);
        device.Ip = ip;
        device.CachedHostPort = hostPort;
        device.CachedAt = DateTimeOffset.UtcNow;
        await configService.SaveAsync();
        return Results.NoContent();
    }

    private async Task<IResult> PairDeviceAsync(string name, CompanionPairRequest request, HttpContext ctx)
    {
        if (request.Port is < 1 or > 65535 || request.Code is not { Length: 6 } code || !code.All(char.IsAsciiDigit))
            return Results.BadRequest("expected a port and a 6-digit code");

        var config = await configService.GetAsync();
        var device = config.Devices.FirstOrDefault(d => d.Name == name);
        if (device is null)
            return Results.NotFound();
        if (device.Ip is null || ctx.Connection.RemoteIpAddress is not { } remote)
            return Results.BadRequest("not a wireless device");

        device.Ip = remote.MapToIPv4().ToString();
        logger.LogInformation("Companion app requested pairing for '{Device}' at {Ip}:{Port}", name, device.Ip, request.Port);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, _stopping.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await deviceResolver.PairAtAsync(device, request.Port, code, timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_stopping.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Companion pairing for '{Device}' failed", name);
            return Results.UnprocessableEntity(ex.Message);
        }

        await configService.SaveAsync();
        logger.LogInformation("Paired with '{Device}'", name);
        return Results.Ok();
    }

    private async Task<IResult> StopJobAsync(string name)
    {
        var config = await configService.GetAsync();
        var index = config.Jobs.FindIndex(j => j.Name == name);
        if (index < 0)
            return Results.NotFound();

        var cancelled = jobRunService.CancelJob(index);
        cancelled |= changeWatch.CancelJob(name);
        logger.LogInformation("Companion app requested a stop of '{Job}' (was running: {Cancelled})", name, cancelled);
        return cancelled ? Results.Accepted() : Results.Conflict();
    }
}
