using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Merge;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Orchestration;
using System.Globalization;

namespace AdbSync.Core.Services.Merge;

public enum ConflictChoice
{
    /// <summary>Keep what master (the PC and every other device) has.</summary>
    Master,

    /// <summary>Keep the version from the device the conflict was found on.</summary>
    Device,

    /// <summary>Keep master's version and also save the device's as a separate file next to it.</summary>
    Both,

    /// <summary>Keep whichever side was modified last; an edit beats a deletion.</summary>
    Newest,
}

/// <summary>Where each side of a pending conflict can be read on the PC; null when that side deleted the file.</summary>
public sealed record ConflictFiles(string? MasterPath, string? DeviceCopyPath);

public sealed class ConflictResolutionException(string message) : Exception(message);

public interface IConflictResolver
{
    /// <summary>Applies the choice to master and the device's manifest so the next sync carries it everywhere. Returns the job to run next.</summary>
    Task<string> ResolveAsync(string conflictId, ConflictChoice choice, CancellationToken ct = default);

    Task<ConflictFiles?> GetFilesAsync(string conflictId, CancellationToken ct = default);
}

public sealed class ConflictResolver(
    IAppConfigStore configStore,
    IPendingConflictStore pending,
    IManifestStore manifests,
    ISyncLockManager lockManager) : IConflictResolver
{
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(20);

    public async Task<string> ResolveAsync(string conflictId, ConflictChoice choice, CancellationToken ct = default)
    {
        var conflict = await pending.GetAsync(conflictId, ct)
            ?? throw new ConflictResolutionException("That conflict has already been resolved.");

        var config = await configStore.LoadAsync(ct);
        var job = config.Jobs.FirstOrDefault(j => j.Name == conflict.JobName)
            ?? throw new ConflictResolutionException($"Job '{conflict.JobName}' no longer exists.");
        var eff = job.Resolve(config.Settings);
        var projectRoot = ProjectFolders.ProjectRoot(eff, job.Name);
        if (ProjectFolders.Unavailable(eff, job.Name) is { } unavailable)
            throw new ConflictResolutionException(unavailable);
        var masterPath = Path.Combine(projectRoot, "master");

        await using var @lock = await AcquireAsync(projectRoot, TimeSpan.FromHours(eff.StaleLockHours), ct)
            ?? throw new ConflictResolutionException($"'{job.Name}' is syncing right now - try again in a moment.");

        var masterFile = Path.Combine(masterPath, conflict.RelativePath);
        var deviceCopy = pending.GetDeviceCopyPath(conflict);
        if (conflict.DeviceModifiedUtc is not null && deviceCopy is null)
            throw new ConflictResolutionException("The saved copy of the device's file is missing - run the sync again to refresh it.");

        if (choice == ConflictChoice.Newest)
            choice = PickNewest(conflict);

        var manifest = await manifests.TryGetAsync(conflict.JobName, conflict.DeviceName, ct) ?? new SyncManifest();
        var backupDir = Path.Combine(projectRoot, ".sync_conflicts", ConfigNames.Segment(conflict.DeviceName));

        if (choice == ConflictChoice.Device)
        {
            if (File.Exists(masterFile))
                Backup(masterFile, backupDir, conflict.RelativePath);

            if (deviceCopy is null)
            {
                if (File.Exists(masterFile))
                    File.Delete(masterFile);
            }
            else
            {
                CopyWithTime(deviceCopy, masterFile, conflict.DeviceModifiedUtc!.Value);
                manifest.Entries[conflict.RelativePath] = new ManifestEntry(conflict.DeviceSize!.Value, conflict.DeviceModifiedUtc.Value);
            }
        }
        else
        {
            if (deviceCopy is not null)
            {
                Backup(deviceCopy, backupDir, conflict.RelativePath);
                if (choice == ConflictChoice.Both)
                    CopyWithTime(deviceCopy, SiblingPath(masterFile, conflict.DeviceName), conflict.DeviceModifiedUtc!.Value);
                manifest.Entries[conflict.RelativePath] = new ManifestEntry(conflict.DeviceSize!.Value, conflict.DeviceModifiedUtc!.Value);
            }
            else
            {
                manifest.Entries.Remove(conflict.RelativePath);
            }
        }

        await manifests.SaveAsync(conflict.JobName, conflict.DeviceName, manifest, ct);
        await pending.RemoveAsync(conflict, ct);
        return conflict.JobName;
    }

    public async Task<ConflictFiles?> GetFilesAsync(string conflictId, CancellationToken ct = default)
    {
        var conflict = await pending.GetAsync(conflictId, ct);
        if (conflict is null)
            return null;
        var config = await configStore.LoadAsync(ct);
        var job = config.Jobs.FirstOrDefault(j => j.Name == conflict.JobName);
        if (job is null)
            return null;

        var masterFile = ProjectFolders.TryProjectRoot(job.Resolve(config.Settings), job.Name) is { } projectRoot
            ? Path.Combine(projectRoot, "master", conflict.RelativePath)
            : null;
        return new ConflictFiles(File.Exists(masterFile) ? masterFile : null, pending.GetDeviceCopyPath(conflict));
    }

    private async Task<IAsyncDisposable?> AcquireAsync(string projectRoot, TimeSpan staleAfter, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + LockWait;
        while (true)
        {
            var handle = await lockManager.TryAcquireAsync(projectRoot, staleAfter, ct);
            if (handle is not null || DateTime.UtcNow >= deadline)
                return handle;
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    private static ConflictChoice PickNewest(PendingConflict c)
    {
        if (c.DeviceModifiedUtc is null) return ConflictChoice.Master;
        if (c.MasterModifiedUtc is null) return ConflictChoice.Device;
        return c.DeviceModifiedUtc >= c.MasterModifiedUtc ? ConflictChoice.Device : ConflictChoice.Master;
    }

    private static string SiblingPath(string masterFile, string deviceName)
    {
        var dir = Path.GetDirectoryName(masterFile)!;
        var name = Path.GetFileNameWithoutExtension(masterFile);
        var ext = Path.GetExtension(masterFile);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(dir, $"{name} ({ConfigNames.Segment(deviceName)} {stamp}){ext}");
    }

    private static void Backup(string source, string backupDir, string relativePath)
    {
        Directory.CreateDirectory(backupDir);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        File.Copy(source, Path.Combine(backupDir, $"{Path.GetFileName(relativePath)}.{stamp}.conflict"), overwrite: true);
    }

    private static void CopyWithTime(string source, string dest, DateTimeOffset modifiedUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var temp = $"{dest}.tmp-{Guid.NewGuid():N}";
        File.Copy(source, temp, overwrite: true);
        File.SetLastWriteTimeUtc(temp, modifiedUtc.UtcDateTime);
        File.Move(temp, dest, overwrite: true);
    }
}
