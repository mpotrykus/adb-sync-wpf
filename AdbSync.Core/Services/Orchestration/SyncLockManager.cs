using AdbSync.Core.Services.Common;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AdbSync.Core.Services.Orchestration;

/// <summary>
/// Cooperative per-project lock, preserving the old tool's on-disk contract: a text file at
/// "&lt;projectRoot&gt;\.sync_staging\.sync_lock" containing "pid=&lt;pid&gt;\nstart=&lt;iso8601&gt;". A lock is
/// reclaimed if it's corrupt/unparsable, older than staleAfter, or its owning PID is no longer running - matching
/// a live, running PID is the only thing that blocks acquisition.
/// </summary>
public sealed class SyncLockManager : ISyncLockManager
{
    public async Task<IAsyncDisposable?> TryAcquireAsync(string projectRoot, TimeSpan staleAfter, CancellationToken ct = default)
    {
        var stagingRoot = GetStagingRoot(projectRoot);
        var lockPath = GetLockPath(projectRoot);

        Directory.CreateDirectory(stagingRoot);
        if (await TryCreateLockFileAsync(lockPath, ct))
            return new LockHandle(lockPath);

        if (!await IsStaleAsync(lockPath, staleAfter, ct))
            return null;

        DirectoryUtil.DeleteRecursive(stagingRoot);
        Directory.CreateDirectory(stagingRoot);
        return await TryCreateLockFileAsync(lockPath, ct) ? new LockHandle(lockPath) : null;
    }

    private static async Task<bool> TryCreateLockFileAsync(string lockPath, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync($"pid={Environment.ProcessId}\nstart={DateTimeOffset.UtcNow:o}".AsMemory(), ct);
            return true;
        }
        catch (IOException) when (File.Exists(lockPath))
        {
            return false;
        }
    }

    public static string GetStagingRoot(string projectRoot) => Path.Combine(projectRoot, ".sync_staging");
    public static string GetLockPath(string projectRoot) => Path.Combine(GetStagingRoot(projectRoot), ".sync_lock");

    private static async Task<bool> IsStaleAsync(string lockPath, TimeSpan staleAfter, CancellationToken ct)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(lockPath, ct);
        }
        catch (IOException)
        {
            // Unreadable usually means another acquirer is mid-write; treating it as stale would wipe a live run's staging.
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath) > staleAfter;
        }

        var pidMatch = Regex.Match(content, @"pid=(\d+)");
        if (!pidMatch.Success)
            return true;

        if (Regex.Match(content, @"start=(.+)") is { Success: true } startMatch
            && DateTimeOffset.TryParse(startMatch.Groups[1].Value.Trim(), out var start)
            && DateTimeOffset.UtcNow - start > staleAfter)
        {
            return true;
        }

        try
        {
            Process.GetProcessById(int.Parse(pidMatch.Groups[1].Value));
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private sealed class LockHandle(string lockPath) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            if (File.Exists(lockPath))
            {
                File.SetAttributes(lockPath, File.GetAttributes(lockPath) & ~FileAttributes.ReadOnly);
                File.Delete(lockPath);
            }
            return ValueTask.CompletedTask;
        }
    }
}
