using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Server.Events;
using System.Collections.Concurrent;

namespace AdbSync.Server.Api;

public sealed record WatchStateDto(Guid DeviceId, string State, string? Detail, DateTimeOffset? Since);

/// <summary>
/// The on-change watcher's state per job and device, from the engine's watch events (it doesn't keep one itself).
/// Publishes <c>watch.changed</c>. Keyed by name, as the events are; ids are attached when read.
/// </summary>
public sealed class WatchStateTracker(EventBus bus, Engine.Services.AppConfigService config) : ISyncEventSink
{
    private readonly ConcurrentDictionary<(string Job, string Device), (string State, string? Detail, DateTimeOffset Since)> _states = new();

    public IReadOnlyList<WatchStateDto> For(Core.Models.Config.SyncJobConfig job, Core.Models.Config.AppConfig appConfig) =>
        job.Devices.Select(b =>
        {
            var deviceId = appConfig.Devices.FirstOrDefault(d => string.Equals(d.Name, b.DeviceName, StringComparison.OrdinalIgnoreCase))?.Id ?? b.DeviceId;
            if (job.Schedule.Kind != Core.Models.Config.ScheduleKind.OnChange || !job.Enabled)
                return new WatchStateDto(deviceId, "off", null, null);
            return _states.TryGetValue((job.Name, b.DeviceName), out var s)
                ? new WatchStateDto(deviceId, s.State, s.Detail, s.Since)
                : new WatchStateDto(deviceId, "stopped", null, null);
        }).ToList();

    public void WatchStarted(string jobName, string deviceName, bool liveWatch) => Set(jobName, deviceName, liveWatch ? "live" : "polling", null);
    public void WatchDegraded(string jobName, string deviceName, string reason) => Set(jobName, deviceName, "degraded", reason);
    public void WatchStopped(string jobName, string deviceName) => Set(jobName, deviceName, "stopped", null);

    private async void Set(string jobName, string deviceName, string state, string? detail)
    {
        _states[(jobName, deviceName)] = (state, detail, DateTimeOffset.UtcNow);
        try
        {
            var appConfig = await config.GetAsync();
            if (appConfig.Jobs.FirstOrDefault(j => j.Name == jobName) is { } job)
                bus.Publish("watch.changed", new { jobId = job.Id, watch = For(job, appConfig) });
        }
        catch (Exception)
        {
        }
    }

    public void PhaseChanged(string jobName, SyncPhase phase, string? deviceName = null) { }
    public void JobQueued(string jobName, string reason) { }
    public void JobSkipped(string jobName, string reason) { }
    public void JobCompleted(string jobName, bool pushed) { }
    public void JobFailed(string jobName, Exception exception) { }
    public void JobCancelled(string jobName) { }
    public void MergeConflictsDetected(string jobName, string deviceName, int conflictCount) { }
    public void ChangeDetected(string jobName, string deviceName) { }
}
