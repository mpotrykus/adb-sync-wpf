using AdbSync.Core.Models.Orchestration;

namespace AdbSync.Core.Services.Companion;

public sealed record StaleDevice(string Device, string Reason);

/// <summary>Works out which of a job's devices are behind the others right now, so opening the linked app there
/// (and saving) could overwrite changes from elsewhere. A device is only reported while no sync is actively
/// carrying changes across; once a run starts transferring, the push will bring it up to date.</summary>
public static class SyncStaleness
{
    public static IReadOnlyList<StaleDevice> Assess(IReadOnlyList<string> jobDevices, JobStatus status, SyncCheckpoint? checkpoint)
    {
        if (status.Running && status.BlockedDevice is null && status.Phase != "Queued")
            return [];

        var stale = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var source in status.PendingChangeDevices ?? [])
        {
            foreach (var device in jobDevices.Where(d => d != source))
                stale.TryAdd(device, $"{source} has changes that haven't synced yet");
        }

        if (status.BlockedDevice is { } blocked)
        {
            foreach (var device in jobDevices.Where(d => d != blocked))
                stale.TryAdd(device, $"{blocked} has the app open and its changes haven't synced");
        }

        if (checkpoint is not null)
        {
            var done = checkpoint.Phase == SyncPhase.Push
                ? checkpoint.CompletedDevices.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            foreach (var device in jobDevices.Where(d => !done.Contains(d)))
                stale.TryAdd(device, "an earlier sync was interrupted before it finished updating this device");
        }

        return jobDevices.Where(stale.ContainsKey).Select(d => new StaleDevice(d, stale[d])).ToList();
    }
}
