using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Merge;
using AdbSync.Core.Services.Merge;
using AdbSync.Engine.Services;
using AdbSync.Server.Events;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace AdbSync.Server.Api;

public static class ConflictEndpoints
{
    private static readonly HashSet<string> PreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    public static void MapConflictEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/conflicts", async (Guid? jobId, AppConfigService configService, IPendingConflictStore pending, IConflictResolver resolver, CancellationToken ct) =>
        {
            var config = await configService.GetAsync();
            var jobName = jobId is { } id ? config.Jobs.FirstOrDefault(j => j.Id == id)?.Name : null;
            if (jobId is not null && jobName is null)
                return Problems.NotFound("No job with that id.");
            var result = new List<ConflictDto>();
            foreach (var conflict in await pending.ListAsync(jobName, ct))
                result.Add(await ToDtoAsync(conflict, config, resolver, ct));
            return Results.Ok(result);
        });

        api.MapGet("/conflicts/{conflictId}", async (string conflictId, AppConfigService configService, IPendingConflictStore pending, IConflictResolver resolver, CancellationToken ct) =>
            await pending.GetAsync(conflictId, ct) is { } conflict
                ? Results.Ok(await ToDtoAsync(conflict, await configService.GetAsync(), resolver, ct))
                : Problems.NotFound("No pending conflict with that id."));

        api.MapGet("/conflicts/{conflictId}/thumbnail", async (string conflictId, string? side, int? maxSize, IPendingConflictStore pending, IConflictResolver resolver, CancellationToken ct) =>
        {
            if (side is not ("master" or "device"))
                return Problems.Validation("side must be master or device.");
            if (await pending.GetAsync(conflictId, ct) is not { } conflict)
                return Problems.NotFound("No pending conflict with that id.");
            var files = await resolver.GetFilesAsync(conflictId, ct);
            var path = side == "master" ? files?.MasterPath : files?.DeviceCopyPath;
            if (path is null || !PreviewExtensions.Contains(Path.GetExtension(conflict.RelativePath)))
                return NoPreview();

            try
            {
                using var image = await Image.LoadAsync(path, ct);
                var size = Math.Clamp(maxSize ?? 320, 32, 1024);
                image.Mutate(x => x.AutoOrient().Resize(new ResizeOptions { Size = new Size(size, size), Mode = ResizeMode.Max }));
                var output = new MemoryStream();
                await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 80 }, ct);
                return Results.File(output.ToArray(), "image/jpeg");
            }
            catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or IOException)
            {
                return NoPreview();
            }
        });

        api.MapPost("/conflicts/{conflictId}/resolve", async (
            string conflictId, ResolveRequest request, AppConfigService configService, IPendingConflictStore pending,
            IConflictResolver resolver, JobRunService runs, EventBus bus, CancellationToken ct) =>
        {
            if (ParseChoice(request.Choice) is not { } choice)
                return Problems.Validation("choice must be master, device, both or newest.");
            var outcome = await ResolveOneAsync(conflictId, choice, request.Choice!, configService, pending, resolver, runs, bus, ct);
            if (outcome.Failure is { } failure)
                return failure.Code == "conflict_already_resolved"
                    ? Problems.Create(StatusCodes.Status404NotFound, failure.Code, "That conflict has already been resolved.", failure.Detail)
                    : Problems.Create(StatusCodes.Status409Conflict, failure.Code, "The conflict wasn't resolved.", failure.Detail);
            await QueueFollowUpRunsAsync([outcome.JobName!], configService, runs);
            return Results.Ok(new ResolveResultDto(1, []));
        });

        api.MapPost("/conflicts/resolve", async (
            ResolveManyRequest request, AppConfigService configService, IPendingConflictStore pending,
            IConflictResolver resolver, JobRunService runs, EventBus bus, CancellationToken ct) =>
        {
            if (ParseChoice(request.Choice) is not { } choice)
                return Problems.Validation("choice must be master, device, both or newest.");
            var config = await configService.GetAsync();
            var jobName = request.JobId is { } jobId ? config.Jobs.FirstOrDefault(j => j.Id == jobId)?.Name : null;
            var ids = request.Ids is { Count: > 0 } given
                ? given.ToList()
                : (await pending.ListAsync(jobName, ct)).Select(c => c.Id).ToList();

            var failed = new List<ResolveFailure>();
            var jobs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var outcome = await ResolveOneAsync(id, choice, request.Choice!, configService, pending, resolver, runs, bus, ct);
                if (outcome.Failure is { } failure)
                    failed.Add(failure);
                else
                    jobs.Add(outcome.JobName!);
            }
            await QueueFollowUpRunsAsync(jobs, configService, runs);
            return Results.Ok(new ResolveResultDto(ids.Count - failed.Count, failed));
        });
    }

    private static async Task<(string? JobName, ResolveFailure? Failure)> ResolveOneAsync(
        string id, ConflictChoice choice, string choiceName, AppConfigService configService, IPendingConflictStore pending,
        IConflictResolver resolver, JobRunService runs, EventBus bus, CancellationToken ct)
    {
        if (await pending.GetAsync(id, ct) is not { } conflict)
            return (null, new ResolveFailure(id, "conflict_already_resolved", "That conflict has already been resolved."));
        var config = await configService.GetAsync();
        var jobIndex = config.Jobs.FindIndex(j => j.Name == conflict.JobName);
        if (jobIndex >= 0 && runs.IsRunning(jobIndex))
            return (null, new ResolveFailure(id, "job_running", $"'{conflict.JobName}' is syncing right now. Try again when it finishes."));
        try
        {
            var jobName = await resolver.ResolveAsync(id, choice, ct);
            bus.Publish("conflict.resolved", new { id, jobId = jobIndex >= 0 ? config.Jobs[jobIndex].Id : Guid.Empty, choice = choiceName });
            return (jobName, null);
        }
        catch (ConflictResolutionException ex)
        {
            return (null, new ResolveFailure(id, "internal_error", ex.Message));
        }
    }

    /// <summary>The resolution changed master and the manifest; the job's next run carries it to every device.</summary>
    private static async Task QueueFollowUpRunsAsync(IEnumerable<string> jobNames, AppConfigService configService, JobRunService runs)
    {
        var config = await configService.GetAsync();
        foreach (var name in jobNames)
        {
            var index = config.Jobs.FindIndex(j => j.Name == name);
            if (index >= 0)
                _ = Task.Run(() => runs.RunJobAsync(index, trigger: RunTrigger.ConflictResolved));
        }
    }

    public static async Task<ConflictDto> ToDtoAsync(PendingConflict conflict, AppConfig config, IConflictResolver resolver, CancellationToken ct)
    {
        var files = await resolver.GetFilesAsync(conflict.Id, ct);
        var previewable = PreviewExtensions.Contains(Path.GetExtension(conflict.RelativePath));
        return new ConflictDto(
            conflict.Id,
            config.Jobs.FirstOrDefault(j => j.Name == conflict.JobName)?.Id ?? Guid.Empty,
            config.Devices.FirstOrDefault(d => string.Equals(d.Name, conflict.DeviceName, StringComparison.OrdinalIgnoreCase))?.Id ?? Guid.Empty,
            conflict.RelativePath,
            conflict.DetectedAtUtc,
            new ConflictSideInfoDto(conflict.MasterSize is null, conflict.MasterSize, conflict.MasterModifiedUtc, previewable && files?.MasterPath is not null),
            new ConflictSideInfoDto(conflict.DeviceSize is null, conflict.DeviceSize, conflict.DeviceModifiedUtc, previewable && files?.DeviceCopyPath is not null));
    }

    private static ConflictChoice? ParseChoice(string? choice) => choice switch
    {
        "master" => ConflictChoice.Master,
        "device" => ConflictChoice.Device,
        "both" => ConflictChoice.Both,
        "newest" => ConflictChoice.Newest,
        _ => null,
    };

    private static IResult NoPreview() =>
        Problems.Create(StatusCodes.Status404NotFound, "no_preview", "There's no preview for that side.");
}
