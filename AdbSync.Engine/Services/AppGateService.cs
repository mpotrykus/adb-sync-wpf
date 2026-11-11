using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Companion;
using AdbSync.Core.Services.Devices;
using Microsoft.Extensions.Logging;

namespace AdbSync.Engine.Services;

public sealed record CloseAndSyncResult(string Result, bool Failed, bool StillOutOfSync, string? Reason);

/// <summary>Actions around a job's linked app: which devices are behind, launching the app on a device, and
/// closing it so a blocked sync can go ahead.</summary>
public sealed class AppGateService(
    AppConfigService configService,
    JobStatusTracker tracker,
    JobRunService jobRunService,
    IAdbDeviceResolver deviceResolver,
    IAppRunningGuard appGuard,
    IDeviceAppControl appControl,
    ILogger<AppGateService> logger)
{
    private static readonly TimeSpan MaxSyncWait = TimeSpan.FromMinutes(20);

    public async Task<IReadOnlyList<StaleDevice>> AssessAsync(string jobName)
    {
        var config = await configService.GetAsync();
        var job = config.Jobs.FirstOrDefault(j => j.Name == jobName);
        if (job is null)
            return [];

        var checkpoint = await jobRunService.GetCheckpointAsync(jobName);
        return SyncStaleness.Assess(job.Devices.Select(b => b.DeviceName).ToList(), tracker.Get(jobName), checkpoint);
    }

    /// <summary>Starts the job's app on <paramref name="deviceName"/>. Returns false if the job has no linked app,
    /// the device isn't bound to it, or the device has no launcher activity for the package.</summary>
    public async Task<bool> OpenAppAsync(string jobName, string deviceName, CancellationToken ct = default)
    {
        var config = await configService.GetAsync();
        var job = config.Jobs.FirstOrDefault(j => j.Name == jobName);
        var device = config.Devices.FirstOrDefault(d => d.Name == deviceName);
        if (job?.AppPackage is not { } package || device is null || job.Devices.All(b => b.DeviceName != deviceName))
            return false;

        var serial = await deviceResolver.EnsureConnectedAsync(device, ct);
        await configService.SaveAsync();
        return await appControl.LaunchAsync(package, serial, ct);
    }

    /// <summary>Force-stops the linked app on <paramref name="deviceName"/> (or, when null, on every device that
    /// has it running), then runs the job and waits for it to finish. Anything the app hadn't saved is lost.
    /// Returns null if the job doesn't exist or has no linked app.</summary>
    public async Task<CloseAndSyncResult?> CloseAppAndSyncAsync(string jobName, string? deviceName, CancellationToken ct = default)
    {
        var config = await configService.GetAsync();
        var index = config.Jobs.FindIndex(j => j.Name == jobName);
        if (index < 0 || config.Jobs[index].AppPackage is not { } package)
            return null;

        var job = config.Jobs[index];
        foreach (var binding in job.Devices.Where(b => deviceName is null || b.DeviceName == deviceName))
        {
            var device = config.Devices.FirstOrDefault(d => d.Name == binding.DeviceName);
            if (device is null)
                continue;

            var serial = await deviceResolver.EnsureConnectedAsync(device, ct);
            if (deviceName is not null || await appGuard.FindRunningSerialAsync(package, [serial], ct) is not null)
            {
                logger.LogInformation("Closing {Package} on '{Device}' so '{Job}' can sync", package, binding.DeviceName, jobName);
                await appControl.ForceStopAsync(package, serial, ct);
            }
        }
        await configService.SaveAsync();

        var result = await jobRunService.RunJobAsync(index, ct: ct, trigger: RunTrigger.CloseAndSync);
        if (result.Outcome == JobRunOutcome.Skipped)
        {
            var deadline = DateTimeOffset.UtcNow + MaxSyncWait;
            while (tracker.Get(jobName).Running && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        var status = tracker.Get(jobName);
        var stale = await AssessAsync(jobName);
        var remaining = deviceName is null ? stale.FirstOrDefault() : stale.FirstOrDefault(s => s.Device == deviceName);
        return new CloseAndSyncResult(status.LastResult ?? result.Outcome.ToString(), status.LastFailed, remaining is not null, remaining?.Reason);
    }
}
