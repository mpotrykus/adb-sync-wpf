using Microsoft.Extensions.Hosting;

namespace AdbSync.App.Services;

/// <summary>One ticking timer for every job, rather than one timer per job - simpler, no per-job timer-drift/disposal bugs.</summary>
public sealed class SchedulerHostedService(JobRunService jobRunService) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    public bool Paused { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                catch
                {
                    // Individual job failures already surface via JobRunResult/ISyncEventSink; a hard failure
                    // here (e.g. config file corruption) shouldn't kill the recurring timer loop.
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
