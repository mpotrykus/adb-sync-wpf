using AdbSync.Core.Models.Merge;

namespace AdbSync.Core.Services.Merge;

public interface IPendingConflictStore
{
    Task<IReadOnlyList<PendingConflict>> ListAsync(string? jobName = null, CancellationToken ct = default);

    Task<PendingConflict?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Makes the stored conflicts for (job, device) exactly <paramref name="held"/>: new ones are recorded with a copy of the device's file taken from
    /// <paramref name="stagingPath"/>, changed ones are refreshed, and ones no longer held are dropped. Returns how many were not already pending.</summary>
    Task<int> ReplaceDeviceAsync(string jobName, string deviceName, IReadOnlyList<HeldConflict> held, string stagingPath, CancellationToken ct = default);

    /// <summary>Where the device's copy of the file was saved, or null if the device had deleted it.</summary>
    string? GetDeviceCopyPath(PendingConflict conflict);

    Task RemoveAsync(PendingConflict conflict, CancellationToken ct = default);
}
