using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Orchestration.RunHistory;
using AdbSync.Core.Services.Common;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Orchestration.RunHistory;
using AdbSync.Core.Services.Persistence;
using AdbSync.Engine.Services;
using AdbSync.Server.Auth;
using AdbSync.Server.Events;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdbSync.Server.Api;

/// <summary>Serialises config edits from the API; the engine still edits the same config object for runtime state.</summary>
public sealed class ConfigEditor(AppConfigService configService)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> EditAsync<T>(Func<AppConfig, Task<T>> edit)
    {
        await _gate.WaitAsync();
        try
        {
            return await edit(await configService.GetAsync());
        }
        finally
        {
            _gate.Release();
        }
    }
}

public static class JobEndpoints
{
    public static void MapJobEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/jobs", async (AppConfigService configService) =>
        {
            var config = await configService.GetAsync();
            return config.Jobs.Select(j => JobMapping.ToDto(j, config)).ToList();
        });

        api.MapGet("/jobs/{jobId:guid}", async (Guid jobId, HttpContext http, AppConfigService configService) =>
        {
            var config = await configService.GetAsync();
            if (config.Jobs.FirstOrDefault(j => j.Id == jobId) is not { } job)
                return JobNotFound();
            var dto = JobMapping.ToDto(job, config);
            http.Response.Headers.ETag = JobMapping.ETag(dto);
            return Results.Ok(dto);
        });

        api.MapPost("/jobs", async (JobInputDto input, HttpContext http, ConfigEditor editor, AppConfigService configService, EventBus bus) =>
            await editor.EditAsync(async config =>
            {
                var errors = JobMapping.Validate(input, config, null);
                if (errors.Count > 0)
                    return ValidationProblem(errors);

                var job = new SyncJobConfig();
                JobMapping.Apply(input, job, config);
                config.Jobs.Add(job);
                await configService.SaveAsync();

                var dto = JobMapping.ToDto(job, config);
                bus.Publish("job.changed", dto);
                http.Response.Headers.ETag = JobMapping.ETag(dto);
                return Results.Created($"/api/v1/jobs/{job.Id}", dto);
            }));

        api.MapPatch("/jobs/{jobId:guid}", async (
            Guid jobId, JsonObject patch, HttpContext http, ConfigEditor editor, AppConfigService configService,
            JobRunService runs, JobStateRenamer renamer, EventBus bus, CancellationToken ct) =>
            await editor.EditAsync(async config =>
            {
                if (config.Jobs.FirstOrDefault(j => j.Id == jobId) is not { } job)
                    return JobNotFound();
                var current = JobMapping.ToDto(job, config);
                if (http.Request.Headers.IfMatch.ToString() is { Length: > 0 } ifMatch && ifMatch != JobMapping.ETag(current))
                    return Problems.Create(StatusCodes.Status412PreconditionFailed, "precondition_failed",
                        "The job changed since you loaded it. Reload it and try again.");

                var merged = JsonSerializer.SerializeToNode(JobMapping.ToInput(current), JobMapping.Json)!;
                MergePatch(merged, patch);
                var input = merged.Deserialize<JobInputDto>(JobMapping.Json)!;
                var errors = JobMapping.Validate(input, config, jobId);
                if (errors.Count > 0)
                    return ValidationProblem(errors);

                var oldName = job.Name;
                var renaming = !string.Equals(oldName, input.Name!.Trim(), StringComparison.Ordinal);
                if (renaming)
                {
                    if (runs.IsRunning(config.Jobs.IndexOf(job)))
                        return Problems.Create(StatusCodes.Status409Conflict, "job_running", "Stop the job before renaming it.");
                    if (await RenameProjectFolderAsync(job, config, input, renamer, ct) is { } problem)
                        return problem;
                }

                JobMapping.Apply(input, job, config);
                try
                {
                    await configService.SaveAsync();
                }
                catch when (renaming)
                {
                    await UndoRenameAsync(job, config, oldName, input.Name!.Trim(), renamer);
                    throw;
                }

                var dto = JobMapping.ToDto(job, config);
                bus.Publish("job.changed", dto);
                http.Response.Headers.ETag = JobMapping.ETag(dto);
                return Results.Ok(dto);
            })).Accepts<JsonObject>("application/merge-patch+json", "application/json");

        api.MapDelete("/jobs/{jobId:guid}", async (
            Guid jobId, bool? deleteProjectFolder, ConfigEditor editor, AppConfigService configService, JobRunService runs,
            IManifestStore manifests, ICheckpointManager checkpoints, EventBus bus, CancellationToken ct) =>
            await editor.EditAsync(async config =>
            {
                if (config.Jobs.FirstOrDefault(j => j.Id == jobId) is not { } job)
                    return JobNotFound();
                if (runs.IsRunning(config.Jobs.IndexOf(job)))
                    return Problems.Create(StatusCodes.Status409Conflict, "job_running", "Stop the job before deleting it.");

                var eff = job.Resolve(config.Settings);
                if (deleteProjectFolder == true && ProjectFolders.TryProjectRoot(eff, job.Name) is { } folder
                    && ProjectFolders.Unavailable(eff, job.Name) is null && Directory.Exists(folder))
                    DirectoryUtil.DeleteRecursive(folder);

                config.Jobs.Remove(job);
                await configService.SaveAsync();
                await manifests.ClearAsync(job.Name, ct);
                await checkpoints.ClearAsync(job.Name, ct);
                bus.Publish("job.deleted", new { id = job.Id });
                return Results.NoContent();
            }));

        api.MapGet("/jobs/{jobId:guid}/status", async (Guid jobId, AppConfigService configService, JobStatusBuilder builder, CancellationToken ct) =>
        {
            var config = await configService.GetAsync();
            return config.Jobs.FirstOrDefault(j => j.Id == jobId) is { } job
                ? Results.Ok(await builder.BuildAsync(job, config, ct))
                : JobNotFound();
        });

        api.MapPost("/jobs/{jobId:guid}/runs", async (
            Guid jobId, HttpContext http, AppConfigService configService, JobRunService runs, RunRegistry registry) =>
        {
            var mode = await ReadModeAsync(http);
            if (mode is null)
                return Problems.Validation("mode must be normal, dryRun or forcePush.");
            var config = await configService.GetAsync();
            var index = config.Jobs.FindIndex(j => j.Id == jobId);
            if (index < 0)
                return JobNotFound();
            if (runs.IsRunning(index))
                return Problems.Create(StatusCodes.Status409Conflict, "job_running", "The job is already running.");
            var job = config.Jobs[index];
            if (ProjectFolders.Unavailable(job.Resolve(config.Settings), job.Name) is { } unavailable)
                return Problems.Create(StatusCodes.Status409Conflict, "project_folder_unavailable", "The project folder isn't available.", unavailable);

            var runId = Guid.NewGuid();
            _ = Task.Run(() => runs.RunJobAsync(index, forcePush: mode == "forcePush", dryRun: mode == "dryRun",
                trigger: RunTrigger.Manual, runId: runId, requestedBy: http.CurrentClientId()));
            var queued = await WaitForQueuedAsync(registry, runId);
            return Results.Accepted($"/api/v1/runs/{runId}", queued);
        }).RequireRunJobs();

        api.MapPost("/jobs/run-all", async (JobRunService runs) =>
        {
            _ = Task.Run(() => runs.RunAllEnabledAsync());
            return Results.Accepted();
        }).RequireRunJobs();

        api.MapPost("/jobs/{jobId:guid}/stop", async (Guid jobId, AppConfigService configService, JobRunService runs, OperationRegistry operations) =>
        {
            var config = await configService.GetAsync();
            var index = config.Jobs.FindIndex(j => j.Id == jobId);
            if (index < 0)
                return JobNotFound();
            return runs.CancelJob(index) | operations.CancelForJob(jobId)
                ? Results.Accepted()
                : Problems.Create(StatusCodes.Status409Conflict, "job_not_running", "The job isn't running.");
        });

        api.MapGet("/jobs/{jobId:guid}/checkpoint", async (Guid jobId, AppConfigService configService, ICheckpointManager checkpoints, CancellationToken ct) =>
        {
            var config = await configService.GetAsync();
            if (config.Jobs.FirstOrDefault(j => j.Id == jobId) is not { } job)
                return JobNotFound();
            return await checkpoints.LoadAsync(job.Name, ct) is { } checkpoint
                ? Results.Ok(new CheckpointSummaryDto(checkpoint.SavedAt, Contract.SyncPhase(checkpoint.Phase),
                    $"{checkpoint.Phase} done on {checkpoint.CompletedDevices.Count} of {job.Devices.Count} devices"))
                : Problems.NotFound("The job has no saved checkpoint.");
        });

        api.MapDelete("/jobs/{jobId:guid}/checkpoint", async (
            Guid jobId, AppConfigService configService, JobRunService runs, ICheckpointManager checkpoints, CancellationToken ct) =>
        {
            var config = await configService.GetAsync();
            var index = config.Jobs.FindIndex(j => j.Id == jobId);
            if (index < 0)
                return JobNotFound();
            if (runs.IsRunning(index))
                return Problems.Create(StatusCodes.Status409Conflict, "job_running", "The job is running.");
            await checkpoints.ClearAsync(config.Jobs[index].Name, ct);
            return Results.NoContent();
        });
    }

    public static void MapRunEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/runs", async (Guid[]? jobId, int? limit, string? cursor, AppConfigService configService, IRunHistoryStore history, RunRegistry registry, CancellationToken ct) =>
        {
            var config = await configService.GetAsync();
            var wanted = jobId is { Length: > 0 } ? jobId.ToHashSet() : null;
            var jobs = config.Jobs.Where(j => wanted is null || wanted.Contains(j.Id)).ToList();
            var active = registry.Active.Where(r => wanted is null || wanted.Contains(r.JobId)).ToDictionary(r => r.Id);
            var runs = new List<RunDto>();
            foreach (var job in jobs)
                runs.AddRange((await history.ListRunsAsync(job.Name, ct)).Select(r => RunRegistry.FromHistory(r, job.Id)));
            for (var i = 0; i < runs.Count; i++)
            {
                if (active.Remove(runs[i].Id, out var live))
                    runs[i] = RunRegistry.WithContext(runs[i], live);
            }
            runs.AddRange(active.Values);

            var ordered = runs.OrderByDescending(r => r.QueuedAt).ThenByDescending(r => r.Id).ToList();
            var start = cursor is not null && int.TryParse(cursor, out var offset) && offset > 0 ? offset : 0;
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var page = ordered.Skip(start).Take(take).ToList();
            return new RunPageDto(page, start + take < ordered.Count ? (start + take).ToString() : null);
        });

        api.MapGet("/runs/{runId:guid}", async (Guid runId, AppConfigService configService, IRunHistoryStore history, RunRegistry registry, ILiveRunStepSink live, CancellationToken ct) =>
        {
            var config = await configService.GetAsync();
            var deviceIds = config.Devices.ToDictionary(d => d.Name, d => d.Id, StringComparer.OrdinalIgnoreCase);
            var activeRun = registry.Get(runId);
            var record = await history.GetRunAsync(runId, ct);
            if (record is null)
                return activeRun is { } running
                    ? Results.Ok(Detail(running, live.TryGet(running.JobName, out var steps) ? steps : [], deviceIds))
                    : Problems.NotFound("No run with that id.");
            var jobIdForRun = config.Jobs.FirstOrDefault(j => j.Name == record.JobName)?.Id ?? Guid.Empty;
            var finished = RunRegistry.FromHistory(record, jobIdForRun);
            if (activeRun is not null)
                finished = RunRegistry.WithContext(finished, activeRun);
            return Results.Ok(Detail(finished, await history.GetRunStepsAsync(runId, ct), deviceIds));
        });

        api.MapGet("/runs/{runId:guid}/log", async (Guid runId, int? tail, IRunHistoryStore history, RunRegistry registry, ILiveRunLogSink live, CancellationToken ct) =>
        {
            string? text;
            if (registry.Get(runId) is { } active)
                text = live.TryGet(active.JobName, out _, out var liveText) ? liveText : null;
            else if (await history.GetRunAsync(runId, ct) is { } record)
                text = await history.GetRunLogAsync(record.JobName, runId, ct);
            else
                return Problems.NotFound("No run with that id.");
            text ??= "";
            if (tail is > 0)
                text = string.Join('\n', text.Split('\n').TakeLast(tail.Value));
            return Results.Text(text, "text/plain");
        });

        api.MapGet("/runs/{runId:guid}/files", async (Guid runId, Guid? deviceId, string? action, AppConfigService configService, IRunHistoryStore history, RunRegistry registry, CancellationToken ct) =>
        {
            if (registry.Get(runId) is not null)
                return Results.Ok(new RunFilesPageDto(false, [], null));
            if (await history.GetRunAsync(runId, ct) is null)
                return Problems.NotFound("No run with that id.");
            var config = await configService.GetAsync();
            var deviceIds = config.Devices.ToDictionary(d => d.Name, d => d.Id, StringComparer.OrdinalIgnoreCase);
            var files = await history.GetRunFilesAsync(runId, ct);
            var items = files
                .Select(f => new RunFileDto(
                    deviceIds.GetValueOrDefault(f.DeviceName),
                    Contract.SyncPhase(f.Phase),
                    f.Path,
                    char.ToLowerInvariant(f.Action.ToString()[0]) + f.Action.ToString()[1..],
                    f.Size,
                    f.Detail))
                .Where(f => (deviceId is null || f.DeviceId == deviceId) && (action is null || f.Action == action))
                .ToList();
            return Results.Ok(new RunFilesPageDto(files.Count > 0, items, null));
        });
    }

    private static RunDetailDto Detail(RunDto run, IReadOnlyList<RunStepRecord> steps, IReadOnlyDictionary<string, Guid> deviceIds) =>
        new(run, RunRegistry.ToDtos(steps, deviceIds));

    private static async Task<RunDto> WaitForQueuedAsync(RunRegistry registry, Guid runId)
    {
        for (var i = 0; i < 50; i++)
        {
            if (registry.Get(runId) is { } run)
                return run;
            await Task.Delay(20);
        }
        return registry.Get(runId) ?? throw new InvalidOperationException("The run didn't start.");
    }

    private static async Task<string?> ReadModeAsync(HttpContext http)
    {
        if (http.Request.ContentLength is null or 0)
            return "normal";
        var body = await JsonSerializer.DeserializeAsync<JsonObject>(http.Request.Body, JobMapping.Json);
        var mode = body?["mode"]?.GetValue<string>() ?? "normal";
        return mode is "normal" or "dryRun" or "forcePush" ? mode : null;
    }

    /// <summary>Moves the job's project folder and its stored state to the new name. Null when that worked.</summary>
    private static async Task<IResult?> RenameProjectFolderAsync(SyncJobConfig job, AppConfig config, JobInputDto input, JobStateRenamer renamer, CancellationToken ct)
    {
        var newName = input.Name!.Trim();
        var eff = job.Resolve(config.Settings);
        var from = ProjectFolders.TryProjectRoot(eff, job.Name);
        var to = ProjectFolders.TryProjectRoot(eff, newName);
        if (from is not null && to is not null && Directory.Exists(from))
        {
            if (ProjectFolders.Unavailable(eff, job.Name) is { } unavailable)
                return Problems.Create(StatusCodes.Status409Conflict, "project_folder_unavailable", "The project folder isn't available.", unavailable);
            if (Directory.Exists(to) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                return Problems.Create(StatusCodes.Status409Conflict, "folder_exists", $"A folder named '{newName}' already exists in the projects directory.");
            Directory.Move(from, to);
        }
        await renamer.RenameAsync(job.Name, newName, ct);
        return null;
    }

    private static async Task UndoRenameAsync(SyncJobConfig job, AppConfig config, string oldName, string newName, JobStateRenamer renamer)
    {
        job.Name = oldName;
        var eff = job.Resolve(config.Settings);
        if (ProjectFolders.TryProjectRoot(eff, newName) is { } moved && Directory.Exists(moved) && ProjectFolders.TryProjectRoot(eff, oldName) is { } original)
            Directory.Move(moved, original);
        await renamer.RenameAsync(newName, oldName);
    }

    /// <summary>RFC 7396: nulls remove a member, objects merge recursively, anything else (arrays included) replaces.</summary>
    private static void MergePatch(JsonNode target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObject || target is not JsonObject targetObject)
            return;
        foreach (var (key, value) in patchObject.ToList())
        {
            if (value is null)
                targetObject.Remove(key);
            else if (value is JsonObject && targetObject[key] is JsonObject existing)
                MergePatch(existing, value);
            else
                targetObject[key] = value.DeepClone();
        }
    }

    private static IResult JobNotFound() => Problems.NotFound("No job with that id.");

    private static IResult ValidationProblem(Dictionary<string, string[]> errors) =>
        Results.Problem(
            title: "The job isn't valid.",
            statusCode: StatusCodes.Status400BadRequest,
            type: "https://adbsync.dev/problems/validation_failed",
            extensions: new Dictionary<string, object?> { ["code"] = "validation_failed", ["errors"] = errors });
}
