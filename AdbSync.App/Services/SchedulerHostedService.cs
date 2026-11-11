using AdbSync.Core.Services.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdbSync.App.Services;

/// <summary>One ticking timer for every job, rather than one timer per job - simpler, no per-job timer-drift/disposal bugs.</summary>
public sealed class SchedulerHostedService(
    JobRunService jobRunService,
    SchedulerStateStore state,
    ILogger<SchedulerHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    public bool Paused { get; private set; }

    public async Task SetPausedAsync(bool paused)
    {
        Paused = paused;
        try
        {
            await state.SetPausedAsync(paused);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't save the scheduler pause state");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Paused = await state.IsPausedAsync(stoppingToken);
        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            if (!Paused)
            {
                try
                {
                    await jobRunService.RunDueJobsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Scheduler tick failed");
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
