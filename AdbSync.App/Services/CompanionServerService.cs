using AdbSync.App.ViewModels;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Merge;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Companion;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Scheduling;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Merge;
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
    DateTimeOffset? LastResultAt,
    DateTimeOffset? NextRunAt,
    bool NeedsAttention,
    bool HasCheckpoint,
    string? CheckpointSummary,
    string? AppPackage,
    IReadOnlyList<StaleDevice> StaleDevices,
    int PendingConflicts);

public sealed record CompanionConflictDto(
    string Id,
    string Job,
    string Device,
    string Path,
    DateTimeOffset DetectedAt,
    bool DeviceDeleted,
    long? DeviceSize,
    DateTimeOffset? DeviceModifiedAt,
    bool PcDeleted,
    long? PcSize,
    DateTimeOffset? PcModifiedAt);

public sealed record CompanionResolveRequest(string Choice);

public sealed record CompanionResolveAllRequest(string Choice, string? Job);

public sealed record CompanionResolveAllResult(int Resolved, IReadOnlyList<string> Failed);

public sealed record CompanionCloseAndSyncRequest(string Device);

public sealed record CompanionEnabledRequest(bool Enabled);

public sealed record CompanionStatusDto(string Pc, IReadOnlyList<CompanionJobDto> Jobs);

public sealed record CompanionRunDto(
    Guid RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Outcome,
    string? ErrorMessage,
    int FilesCopied,
    int FilesDeleted,
    int ErrorCount,
    long BytesCopied);

public sealed record CompanionEndpointReport(int Port);

public sealed record CompanionPairRequest(int Port, string Code);

/// <summary>LAN HTTPS feed for the companion app: a status snapshot, an SSE stream of job changes, the job's log
/// tail, and run/stop actions. Every request needs the bearer token from <c>GlobalSettings.CompanionServerToken</c>.</summary>
public sealed class CompanionServerService(
    AppConfigService configService,
    JobStatusTracker tracker,
    JobRunService jobRunService,
    AppGateService appGate,
    ChangeWatchHostedService changeWatch,
    IRunHistoryStore runHistory,
    ILiveRunLogSink liveLog,
    CompanionCertificateStore certificates,
    IAdbDeviceResolver deviceResolver,
    IPendingConflictStore pendingConflicts,
    IConflictResolver conflictResolver,
    DashboardUiStateStore uiStateStore,
    ILogger<CompanionServerService> logger) : IHostedService
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int LogTailLines = 200;
    private const int HistoryLimit = 50;

    private WebApplication? _app;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<string, CancellationTokenSource> _pendingRuns = new(StringComparer.Ordinal);
    private static readonly TimeSpan RunAfterResolveDelay = TimeSpan.FromSeconds(6);

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
        app.MapGet("/api/jobs/{name}/history", GetHistoryAsync);
        app.MapGet("/api/jobs/{name}/history/{runId:guid}/log", GetRunLogAsync);
        app.MapPost("/api/jobs/{name}/run", RunJobAsync);
        app.MapPost("/api/jobs/{name}/stop", StopJobAsync);
        app.MapPost("/api/jobs/{name}/dryrun", DryRunJobAsync);
        app.MapPost("/api/jobs/{name}/enabled", SetEnabledAsync);
        app.MapPost("/api/jobs/{name}/checkpoint/discard", DiscardCheckpointAsync);
        app.MapPost("/api/jobs/{name}/close-and-sync", CloseAndSyncAsync);
        app.MapGet("/api/conflicts", GetConflictsAsync);
        app.MapGet("/api/conflicts/{id}/thumb/{side}", GetConflictThumbnailAsync);
        app.MapPost("/api/conflicts/{id}/resolve", ResolveConflictAsync);
        app.MapPost("/api/conflicts/resolve-all", ResolveAllConflictsAsync);
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
        var checkpoints = await GetCheckpointsAsync();
        var counts = await GetPendingCountsAsync();
        var jobs = SortLikeDashboard(
            config.Jobs.Select(j => ToDto(tracker.Get(j.Name), config, checkpoints, counts)), await uiStateStore.LoadAsync());
        return new CompanionStatusDto(Environment.MachineName, jobs);
    }

    private static List<CompanionJobDto> SortLikeDashboard(IEnumerable<CompanionJobDto> jobs, DashboardUiState state)
    {
        var descending = state.SortDescending;
        IOrderedEnumerable<CompanionJobDto> Order<TKey>(Func<CompanionJobDto, TKey> key, IComparer<TKey>? comparer = null) =>
            descending ? jobs.OrderByDescending(key, comparer) : jobs.OrderBy(key, comparer);

        return (state.SortColumn switch
        {
            nameof(JobStatusViewModel.Enabled) => Order(j => j.Enabled),
            nameof(JobStatusViewModel.DisplayStatusText) => Order(j => j.Running ? j.Phase ?? "" : "Idle", StringComparer.CurrentCultureIgnoreCase),
            nameof(JobStatusViewModel.LastRunAt) => Order(j => j.LastResultAt),
            nameof(JobStatusViewModel.NextRunAt) => Order(j => j.NextRunAt),
            _ => Order(j => j.Name, StringComparer.CurrentCultureIgnoreCase),
        }).ToList();
    }

    private async Task<Dictionary<string, SyncCheckpoint>> GetCheckpointsAsync()
    {
        var all = await jobRunService.GetAllCheckpointsAsync();
        return all.Where(c => c.ProjectName is not null).GroupBy(c => c.ProjectName!).ToDictionary(g => g.Key, g => g.First());
    }

    private async Task<Dictionary<string, int>> GetPendingCountsAsync() =>
        (await pendingConflicts.ListAsync()).GroupBy(c => c.JobName).ToDictionary(g => g.Key, g => g.Count());

    private static CompanionJobDto ToDto(
        JobStatus s, AppConfig config, IReadOnlyDictionary<string, SyncCheckpoint> checkpoints, IReadOnlyDictionary<string, int> pendingCounts)
    {
        var job = config.Jobs.FirstOrDefault(j => j.Name == s.Name);
        var enabled = job?.Enabled ?? false;
        var nextRun = job is not null && enabled
            ? ScheduleCalculator.NextDueUtc(job.Schedule, DateTimeOffset.Now, config.Settings.RunMissedSchedules)
            : null;
        checkpoints.TryGetValue(s.Name, out var checkpoint);
        return new CompanionJobDto(
            s.Name, enabled, s.Running, s.Phase, s.Devices, s.LastResult, s.LastFailed, s.LastResultAt,
            nextRun, s.LastFailed && !s.Running, checkpoint is not null,
            checkpoint is null ? null : CheckpointSummaryFormatter.Format(checkpoint, job?.Devices.Count ?? 0),
            job?.AppPackage,
            job is null ? [] : SyncStaleness.Assess(job.Devices.Select(b => b.DeviceName).ToList(), s, checkpoint),
            pendingCounts.GetValueOrDefault(s.Name));
    }

    private async Task StreamEventsAsync(HttpContext ctx)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, _stopping.Token);
        var ct = linked.Token;

        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";

        var changes = Channel.CreateBounded<JobStatus?>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        void OnChanged(JobStatus s) => changes.Writer.TryWrite(s);
        void OnSortChanged() => changes.Writer.TryWrite(null);
        tracker.Changed += OnChanged;
        uiStateStore.Saved += OnSortChanged;
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
                var checkpoints = await GetCheckpointsAsync();
                var counts = await GetPendingCountsAsync();
                var resort = false;
                while (changes.Reader.TryRead(out var s))
                {
                    if (s is null)
                    {
                        resort = true;
                        continue;
                    }
                    await WriteEventAsync(ctx, "job", ToDto(s, config, checkpoints, counts), ct);
                }

                if (resort || (await uiStateStore.LoadAsync()).SortColumn is not (null or nameof(JobStatusViewModel.Name)))
                    await WriteEventAsync(ctx, "snapshot", await BuildStatusAsync(), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            tracker.Changed -= OnChanged;
            uiStateStore.Saved -= OnSortChanged;
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

    private async Task<IResult> GetHistoryAsync(string name)
    {
        var config = await configService.GetAsync();
        if (!config.Jobs.Any(j => j.Name == name))
            return Results.NotFound();

        var runs = (await runHistory.ListRunsAsync(name))
            .OrderByDescending(r => r.StartedAt)
            .Take(HistoryLimit)
            .Select(r => new CompanionRunDto(
                r.RunId, r.StartedAt, r.CompletedAt, r.Outcome.ToString(), r.ErrorMessage,
                r.FilesCopied, r.FilesDeleted, r.ErrorCount, r.BytesCopied))
            .ToList();
        return Results.Json(runs, Json);
    }

    private async Task<IResult> GetRunLogAsync(string name, Guid runId)
    {
        var text = await runHistory.GetRunLogAsync(name, runId);
        if (text is null)
            return Results.NotFound();

        var lines = text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        return Results.Text(string.Join('\n', lines.TakeLast(LogTailLines)), "text/plain", Encoding.UTF8);
    }

    private Task<IResult> RunJobAsync(string name) => StartRunAsync(name, dryRun: false);

    private Task<IResult> DryRunJobAsync(string name) => StartRunAsync(name, dryRun: true);

    private async Task<IResult> StartRunAsync(string name, bool dryRun)
    {
        var config = await configService.GetAsync();
        var index = config.Jobs.FindIndex(j => j.Name == name);
        if (index < 0)
            return Results.NotFound();
        if (dryRun && tracker.Get(name).Running)
            return Results.Conflict();

        logger.LogInformation("Companion app requested a {Kind} of '{Job}'", dryRun ? "dry run" : "run", name);
        _ = Task.Run(async () =>
        {
            try
            {
                await jobRunService.RunJobAsync(index, dryRun: dryRun);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Companion-requested run of '{Job}' failed to start", name);
            }
        });
        return Results.Accepted();
    }

    private async Task<IResult> SetEnabledAsync(string name, CompanionEnabledRequest request)
    {
        var config = await configService.GetAsync();
        var job = config.Jobs.FirstOrDefault(j => j.Name == name);
        if (job is null)
            return Results.NotFound();

        if (job.Enabled != request.Enabled)
        {
            job.Enabled = request.Enabled;
            logger.LogInformation("Companion app {Action} '{Job}'", request.Enabled ? "enabled" : "disabled", name);
            await configService.SaveAsync();
        }
        tracker.Republish(name);
        return Results.NoContent();
    }

    /// <summary>The phone's linked app was just opened while this device was behind: close it there, sync, and
    /// report whether the device is now up to date. Holds the request open until the sync finishes.</summary>
    private async Task<IResult> CloseAndSyncAsync(string name, CompanionCloseAndSyncRequest request)
    {
        var config = await configService.GetAsync();
        var job = config.Jobs.FirstOrDefault(j => j.Name == name);
        if (job is null)
            return Results.NotFound();
        if (job.AppPackage is null || job.Devices.All(b => b.DeviceName != request.Device))
            return Results.BadRequest("job has no linked app on that device");

        var status = tracker.Get(name);
        if (status.Running && status.BlockedDevice is null)
            return Results.Conflict("a sync is already in progress");

        logger.LogInformation("Companion app on '{Device}' asked to close the app and sync '{Job}'", request.Device, name);
        var result = await appGate.CloseAppAndSyncAsync(name, request.Device, _stopping.Token);
        return result is null ? Results.NotFound() : Results.Json(result, Json);
    }

    private async Task<IResult> DiscardCheckpointAsync(string name)
    {
        var config = await configService.GetAsync();
        if (!config.Jobs.Any(j => j.Name == name))
            return Results.NotFound();
        if (tracker.Get(name).Running)
            return Results.Conflict();

        logger.LogInformation("Companion app discarded the checkpoint of '{Job}'", name);
        await jobRunService.DiscardCheckpointAsync(name);
        tracker.Republish(name);
        return Results.NoContent();
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

    private async Task<IResult> GetConflictsAsync() =>
        Results.Json((await pendingConflicts.ListAsync()).Select(ToConflictDto).ToList(), Json);

    private static CompanionConflictDto ToConflictDto(PendingConflict c) => new(
        c.Id, c.JobName, c.DeviceName, c.RelativePath, c.DetectedAtUtc,
        c.DeviceModifiedUtc is null, c.DeviceSize, c.DeviceModifiedUtc,
        c.MasterModifiedUtc is null, c.MasterSize, c.MasterModifiedUtc);

    private async Task<IResult> GetConflictThumbnailAsync(string id, string side)
    {
        var files = await conflictResolver.GetFilesAsync(id);
        var path = side switch { "pc" => files?.MasterPath, "device" => files?.DeviceCopyPath, _ => null };
        if (path is null)
            return Results.NotFound();

        var original = (await pendingConflicts.GetAsync(id))?.RelativePath ?? path;
        var bytes = await Task.Run(() => ThumbnailRenderer.RenderJpeg(path, 320, original));
        return bytes is null ? Results.NotFound() : Results.Bytes(bytes, "image/jpeg");
    }

    private static bool TryParseChoice(string? text, out ConflictChoice choice)
    {
        choice = text switch
        {
            "pc" => ConflictChoice.Master,
            "device" => ConflictChoice.Device,
            "both" => ConflictChoice.Both,
            "newest" => ConflictChoice.Newest,
            _ => (ConflictChoice)(-1),
        };
        return Enum.IsDefined(choice);
    }

    private async Task<IResult> ResolveConflictAsync(string id, CompanionResolveRequest request)
    {
        if (!TryParseChoice(request.Choice, out var choice))
            return Results.BadRequest("choice must be pc, device, both or newest");

        try
        {
            var job = await conflictResolver.ResolveAsync(id, choice, _stopping.Token);
            logger.LogInformation("Companion app resolved a conflict in '{Job}' ({Choice})", job, request.Choice);
            tracker.Republish(job);
            RunSoon(job);
            return Results.NoContent();
        }
        catch (ConflictResolutionException ex)
        {
            return Results.Conflict(ex.Message);
        }
    }

    /// <summary>Runs the job shortly after a resolve; another resolve in the window pushes the run back so a burst of taps becomes one sync.</summary>
    private void RunSoon(string job)
    {
        CancellationTokenSource cts;
        lock (_pendingRuns)
        {
            if (_pendingRuns.Remove(job, out var previous))
                previous.Cancel();
            cts = _pendingRuns[job] = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RunAfterResolveDelay, cts.Token);
                lock (_pendingRuns)
                {
                    if (_pendingRuns.TryGetValue(job, out var current) && ReferenceEquals(current, cts))
                        _pendingRuns.Remove(job);
                }
                await StartRunAsync(job, dryRun: false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>Resolves every pending conflict (optionally just one job's) the same way, then runs each affected job once.</summary>
    private async Task<IResult> ResolveAllConflictsAsync(CompanionResolveAllRequest request)
    {
        if (!TryParseChoice(request.Choice, out var choice))
            return Results.BadRequest("choice must be pc, device, both or newest");

        var resolvedJobs = new HashSet<string>(StringComparer.Ordinal);
        var failed = new List<string>();
        var resolved = 0;
        foreach (var c in await pendingConflicts.ListAsync(request.Job))
        {
            try
            {
                resolvedJobs.Add(await conflictResolver.ResolveAsync(c.Id, choice, _stopping.Token));
                resolved++;
            }
            catch (ConflictResolutionException ex)
            {
                failed.Add($"{c.JobName}/{c.RelativePath}: {ex.Message}");
            }
        }

        logger.LogInformation("Companion app resolved {Count} conflict(s) ({Choice}), {Failed} failed", resolved, request.Choice, failed.Count);
        foreach (var job in resolvedJobs)
        {
            tracker.Republish(job);
            await StartRunAsync(job, dryRun: false);
        }
        return Results.Json(new CompanionResolveAllResult(resolved, failed), Json);
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
