using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Services.Transfer;
using AdbSync.Engine.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdbSync.Server.Api;

/// <summary>The job sub-resources besides runs: settings view, manifests, watch, linked app and snapshots.</summary>
public static class JobExtraEndpoints
{
    public static void MapJobExtraEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/jobs/{jobId:guid}/effective-settings", async (Guid jobId, AppConfigService configService) =>
        {
            var config = await configService.GetAsync();
            if (config.Jobs.FirstOrDefault(j => j.Id == jobId) is not { } job)
                return JobNotFound();
            var e = job.Resolve(config.Settings);
            return Results.Ok(new
            {
                projectsDirectory = e.ProjectsDirectory,
                conflictRetentionDays = e.ConflictRetentionDays,
                showInfoNotifications = e.ShowInfoNotifications,
                showErrorNotifications = e.ShowErrorNotifications,
                maxRunHistoryEntries = e.MaxRunHistoryEntries,
                fileDetailRunsKept = e.FileDetailRunsKept,
                backupConflictLosers = e.BackupConflictLosers,
                conflictPolicy = e.ConflictPolicy == ConflictPolicy.Ask ? "ask" : "newestWins",
                checkpointRetentionDays = e.CheckpointRetentionDays,
                rollbackPointsToKeep = e.RollbackPointsToKeep,
                bandwidthThrottleKBps = e.BandwidthThrottleKBps,
                retryMaxAttempts = e.RetryMaxAttempts,
                retryBackoffSeconds = (int)e.RetryBackoff.TotalSeconds,
                pushSafetyMinimumPercent = e.PushSafetyMinimumPercent,
                dryRun = e.DryRun,
            });
        });

        api.MapGet("/jobs/{jobId:guid}/app-icon", async (
            Guid jobId, HttpContext http, AppConfigService configService, IAdbDeviceResolver resolver, AppIconService icons, CancellationToken ct) =>
        {
            var config = await configService.GetAsync();
            if (config.Jobs.FirstOrDefault(j => j.Id == jobId) is not { } job)
                return JobNotFound();
            if (!ApkIcons.IsValidPackageName(job.AppPackage))
                return NoIcon();
            var package = job.AppPackage!;

            if (icons.TryGetCached(package) is { } cached)
            {
                http.Response.Headers.CacheControl = "private, max-age=86400";
                return Results.File(cached, "image/png");
            }
            if (icons.IsKnownMissing(package))
                return NoIcon();

            foreach (var binding in job.Devices)
            {
                if (config.Devices.FirstOrDefault(d => d.Id == binding.DeviceId) is not { } device)
                    continue;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    var serial = await resolver.EnsureConnectedAsync(device, timeout.Token);
                    var fetched = await icons.FetchAsync(package, serial, ct);
                    if (!fetched.PackageFound)
                        continue;
                    if (fetched.Png is null)
                        return NoIcon();
                    http.Response.Headers.CacheControl = "private, max-age=86400";
                    return Results.File(fetched.Png, "image/png");
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                }
            }
            return NoIcon();
        });

        api.MapGet("/jobs/{jobId:guid}/manifests", async (Guid jobId, AppConfigService configService, IManifestStore manifests, CancellationToken ct) =>
        {
            var config = await configService.GetAsync();
            if (config.Jobs.FirstOrDefault(j => j.Id == jobId) is not { } job)
                return JobNotFound();
            var root = await manifests.GetRootAsync(job.Name, ct);
            var result = new List<ManifestSummaryDto>();
            foreach (var binding in job.Devices)
            {
                var manifest = await manifests.TryGetAsync(job.Name, binding.DeviceName, ct);
                result.Add(new ManifestSummaryDto(DeviceId(binding, config), manifest?.Entries.Count ?? 0, manifest?.LastMergedAtUtc, root?.MasterPath));
            }
            return Results.Ok(result);
        });

        api.MapGet("/jobs/{jobId:guid}/watch", async (Guid jobId, AppConfigService configService, WatchStateTracker watch) =>
        {
            var config = await configService.GetAsync();
            return config.Jobs.FirstOrDefault(j => j.Id == jobId) is { } job ? Results.Ok(watch.For(job, config)) : JobNotFound();
        });

        api.MapPost("/jobs/{jobId:guid}/watch/test", async (
            Guid jobId, HttpContext http, AppConfigService configService, IAdbDeviceResolver resolver, IDeviceChangeWatcher watcher, CancellationToken ct) =>
        {
            var config = await configService.GetAsync();
            if (config.Jobs.FirstOrDefault(j => j.Id == jobId) is not { } job)
                return JobNotFound();
            var bindings = job.Devices.Select(b => new JobBindingDto(DeviceId(b, config), b.RemotePath)).ToList();
            if (http.Request.ContentLength is > 0 && await JsonSerializer.DeserializeAsync<JsonObject>(http.Request.Body, JobMapping.Json, ct) is { } body
                && body["bindings"] is JsonArray given)
                bindings = given.Deserialize<List<JobBindingDto>>(JobMapping.Json)!;

            var exclude = new ExcludeMatcher(job.Exclude);
            var results = new List<WatchAvailabilityDto>();
            foreach (var binding in bindings)
            {
                if (config.Devices.FirstOrDefault(d => d.Id == binding.DeviceId) is not { } device)
                {
                    results.Add(new WatchAvailabilityDto(binding.DeviceId, false, "No device with that id.", null));
                    continue;
                }
                try
                {
                    var serial = await resolver.EnsureConnectedAsync(device, ct);
                    var availability = await watcher.CheckAvailabilityAsync(serial, binding.RemotePath, exclude, ct);
                    results.Add(new WatchAvailabilityDto(binding.DeviceId, availability.LiveWatchSupported, availability.Detail, availability.Warning));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    results.Add(new WatchAvailabilityDto(binding.DeviceId, false, $"Couldn't reach the device: {ex.Message}", null));
                }
            }
            return Results.Ok(results);
        });

        api.MapPost("/jobs/{jobId:guid}/app/open", async (Guid jobId, DeviceRefDto request, AppConfigService configService, AppGateService gate, CancellationToken ct) =>
        {
            var config = await configService.GetAsync();
            if (config.Jobs.FirstOrDefault(j => j.Id == jobId) is not { } job)
                return JobNotFound();
            if (config.Devices.FirstOrDefault(d => d.Id == request.DeviceId) is not { } device)
                return Problems.Validation("No device with that id.");
            try
            {
                return await gate.OpenAppAsync(job.Name, device.Name, ct)
                    ? Results.NoContent()
                    : Problems.Validation("The job has no linked app, the device isn't bound to it, or the app can't be launched there.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return Problems.Create(StatusCodes.Status502BadGateway, "device_unreachable", "Couldn't reach the device.", ex.Message);
            }
        });

        api.MapPost("/jobs/{jobId:guid}/app/close-and-sync", async (
            Guid jobId, DeviceRefDto request, AppConfigService configService, AppGateService gate, OperationRegistry operations) =>
        {
            var config = await configService.GetAsync();
            if (config.Jobs.FirstOrDefault(j => j.Id == jobId) is not { } job)
                return JobNotFound();
            if (job.AppPackage is null)
                return Problems.Validation("The job has no linked app.");
            if (config.Devices.FirstOrDefault(d => d.Id == request.DeviceId) is not { } device)
                return Problems.Validation("No device with that id.");

            var runId = Guid.NewGuid();
            var operation = operations.Start("closeAndSync", jobId, async (progress, ct) =>
            {
                progress($"Closing the app on {device.Name}, then syncing", null);
                var result = await gate.CloseAppAndSyncAsync(job.Name, device.Name, ct, runId)
                    ?? throw new OperationFailedException("not_found", "The job or its linked app no longer exists.");
                return new { runId, result = result.Result, stillOutOfSync = result.StillOutOfSync, reason = result.Reason };
            });
            return Results.Accepted($"/api/v1/operations/{operation.Id}", operation);
        });

        api.MapGet("/jobs/{jobId:guid}/snapshots", async (Guid jobId, AppConfigService configService, JobRunService runs, CancellationToken ct) =>
        {
            var config = await configService.GetAsync();
            var index = config.Jobs.FindIndex(j => j.Id == jobId);
            if (index < 0)
                return JobNotFound();
            return Results.Ok((await runs.ListSnapshotsAsync(index, ct)).Select(s => ToDto(s, config.Jobs[index], config)).ToList());
        });

        api.MapPost("/jobs/{jobId:guid}/snapshots", async (Guid jobId, AppConfigService configService, JobRunService runs, OperationRegistry operations) =>
        {
            var config = await configService.GetAsync();
            var index = config.Jobs.FindIndex(j => j.Id == jobId);
            if (index < 0)
                return JobNotFound();
            var operation = operations.Start("createSnapshot", jobId, async (progress, ct) =>
            {
                progress("Pulling every device's current files", null);
                var result = await runs.CreateSnapshotAsync(index, ct);
                var current = await configService.GetAsync();
                var created = (await runs.ListSnapshotsAsync(index, ct)).FirstOrDefault(s => s.Path == result.SnapshotPath);
                return created is null ? null : ToDto(created, current.Jobs[index], current);
            });
            return Results.Accepted($"/api/v1/operations/{operation.Id}", operation);
        });

        api.MapPost("/jobs/{jobId:guid}/snapshots/{snapshotId}/restore", async (
            Guid jobId, string snapshotId, AppConfigService configService, JobRunService runs, OperationRegistry operations, CancellationToken ct) =>
        {
            var config = await configService.GetAsync();
            var index = config.Jobs.FindIndex(j => j.Id == jobId);
            if (index < 0)
                return JobNotFound();
            if (runs.IsRunning(index))
                return Problems.Create(StatusCodes.Status409Conflict, "job_running", "Wait for the job to finish before restoring.");
            var snapshot = (await runs.ListSnapshotsAsync(index, ct)).FirstOrDefault(s => SnapshotId(s) == snapshotId);
            if (snapshot is null)
                return Problems.NotFound("No snapshot with that id.");

            var operation = operations.Start("restoreSnapshot", jobId, async (progress, opCt) =>
            {
                progress("Restoring", null);
                var result = await runs.RestoreSnapshotAsync(index, snapshot.Path, opCt);
                return new { restoredDevices = result.DeviceCount, filesCopied = result.TotalFiles, filesDeleted = result.TotalFilesDeleted, errors = result.Errors };
            });
            return Results.Accepted($"/api/v1/operations/{operation.Id}", operation);
        });
    }

    private static string SnapshotId(SnapshotInfo snapshot) =>
        $"{(snapshot.Kind == SnapshotKind.RollbackPoint ? "rollback" : "manual")}-{Path.GetFileName(snapshot.Path)}";

    private static SnapshotDto ToDto(SnapshotInfo snapshot, SyncJobConfig job, AppConfig config) => new(
        SnapshotId(snapshot),
        job.Id,
        snapshot.Kind == SnapshotKind.RollbackPoint ? "rollbackPoint" : "manual",
        snapshot.CreatedAt,
        snapshot.DeviceNames
            .Select(n => config.Devices.FirstOrDefault(d => string.Equals(d.Name, n, StringComparison.OrdinalIgnoreCase))?.Id)
            .OfType<Guid>().ToList(),
        snapshot.Kind == SnapshotKind.RollbackPoint,
        null,
        snapshot.Path);

    private static Guid DeviceId(JobDeviceBinding binding, AppConfig config) =>
        binding.DeviceId != Guid.Empty
            ? binding.DeviceId
            : config.Devices.FirstOrDefault(d => string.Equals(d.Name, binding.DeviceName, StringComparison.OrdinalIgnoreCase))?.Id ?? Guid.Empty;

    private static IResult JobNotFound() => Problems.NotFound("No job with that id.");

    private static IResult NoIcon() => Problems.Create(StatusCodes.Status404NotFound, "no_icon", "There's no icon for that job's app.");
}
