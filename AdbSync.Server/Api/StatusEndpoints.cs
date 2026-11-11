using AdbSync.Core.Services.Persistence;
using AdbSync.Engine.Services;
using AdbSync.Server.Events;

namespace AdbSync.Server.Api;

public static class StatusEndpoints
{
    public static void MapStatusEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/status", async (
            EventBus bus, AppConfigService configService, JobStatusBuilder builder, SchedulerHostedService scheduler,
            ServerOptions options, CancellationToken ct) =>
        {
            // Read before building, so anything that changes meanwhile is also replayed from /events: applying it twice
            // is harmless, missing it isn't.
            var seq = bus.CurrentSeq;
            var config = await configService.GetAsync();
            var jobs = new List<JobStatusDto>();
            foreach (var job in config.Jobs)
                jobs.Add(await builder.BuildAsync(job, config, ct));
            return new StatusSnapshotDto(seq, SystemEndpoints.Info(options, config), new SchedulerStateDto(scheduler.Paused, null), jobs);
        });

        api.MapGet("/scheduler", (SchedulerHostedService scheduler) => new SchedulerStateDto(scheduler.Paused, null));

        api.MapPatch("/scheduler", async (SchedulerPatch patch, SchedulerHostedService scheduler, EventBus bus) =>
        {
            if (patch.Paused is not { } paused)
                return Problems.Validation("paused is required.");
            await scheduler.SetPausedAsync(paused);
            var state = new SchedulerStateDto(scheduler.Paused, null);
            bus.Publish("scheduler.changed", state);
            return Results.Ok(state);
        }).Accepts<SchedulerPatch>("application/merge-patch+json", "application/json");
    }
}

public sealed record SchedulerPatch(bool? Paused);
