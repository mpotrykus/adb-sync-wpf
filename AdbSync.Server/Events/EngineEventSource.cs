using AdbSync.Core.Services.Companion;
using AdbSync.Engine.Services;
using AdbSync.Server.Api;
using System.Threading.Channels;

namespace AdbSync.Server.Events;

/// <summary>
/// Turns engine changes into events: every change the status tracker reports becomes a <c>job.status</c>, and a config
/// change republishes all of them. Statuses are built on one background loop, in order, because building one reads
/// history and the database and the tracker raises its event from the run's own thread.
/// </summary>
public sealed class EngineEventSource(
    JobStatusTracker tracker, AppConfigService configService, JobStatusBuilder builder, EventBus bus, ILogger<EngineEventSource> logger)
    : BackgroundService
{
    private readonly Channel<string?> _pending = Channel.CreateUnbounded<string?>(new UnboundedChannelOptions { SingleReader = true });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        tracker.Changed += OnTrackerChanged;
        configService.ConfigChanged += OnConfigChanged;
        try
        {
            await foreach (var jobName in _pending.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    var config = await configService.GetAsync();
                    foreach (var job in config.Jobs.Where(j => jobName is null || j.Name == jobName))
                        bus.Publish("job.status", await builder.BuildAsync(job, config, stoppingToken));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Couldn't publish the status of {Job}", jobName ?? "all jobs");
                }
            }
        }
        finally
        {
            tracker.Changed -= OnTrackerChanged;
            configService.ConfigChanged -= OnConfigChanged;
        }
    }

    private void OnTrackerChanged(Core.Services.Companion.JobStatus status) => _pending.Writer.TryWrite(status.Name);

    private void OnConfigChanged(object? sender, EventArgs e) => _pending.Writer.TryWrite(null);
}
