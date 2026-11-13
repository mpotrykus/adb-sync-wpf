using AdbSync.Core.Services.Common;

namespace AdbSync.Core.Services.Orchestration;

/// <summary>
/// Per-project lock: "&lt;projectRoot&gt;/.sync_staging/.sync_lock", held open exclusively for as long as the run lasts.
/// The operating system releases it when the holder exits or crashes, so nothing depends on PIDs (which repeat in
/// containers) or on timeouts. The file is never deleted: on Linux, deleting it on release would let one waiter hold
/// the deleted file while another creates a new one, and both would think they had the lock.
/// </summary>
public sealed class SyncLockManager : ISyncLockManager
{
    public Task<IAsyncDisposable?> TryAcquireAsync(string projectRoot, CancellationToken ct = default)
    {
        var stagingRoot = GetStagingRoot(projectRoot);
        var lockPath = GetLockPath(projectRoot);
        Directory.CreateDirectory(stagingRoot);

        if (TryOpen(lockPath) is not { } stream)
            return Task.FromResult<IAsyncDisposable?>(null);

        // Staging is emptied after every merge and a resumed run doesn't use it, so anything here is left over from
        // a run that died.
        foreach (var entry in Directory.EnumerateFileSystemEntries(stagingRoot))
        {
            if (string.Equals(Path.GetFileName(entry), Path.GetFileName(lockPath), StringComparison.Ordinal))
                continue;
            if (Directory.Exists(entry))
                DirectoryUtil.DeleteRecursive(entry);
            else
                File.Delete(entry);
        }

        return Task.FromResult<IAsyncDisposable?>(stream);
    }

    public static string GetStagingRoot(string projectRoot) => Path.Combine(projectRoot, ".sync_staging");
    public static string GetLockPath(string projectRoot) => Path.Combine(GetStagingRoot(projectRoot), ".sync_lock");

    private static FileStream? TryOpen(string lockPath, bool retried = false)
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException) when (!retried && File.Exists(lockPath))
        {
            // A cloud-synced folder can leave the old lock file ReadOnly.
            File.SetAttributes(lockPath, File.GetAttributes(lockPath) & ~FileAttributes.ReadOnly);
            return TryOpen(lockPath, retried: true);
        }
    }
}
