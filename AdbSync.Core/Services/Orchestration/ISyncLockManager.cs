namespace AdbSync.Core.Services.Orchestration;

public interface ISyncLockManager
{
    /// <summary>Returns null if a live, non-stale process already holds the lock; otherwise an acquired handle whose disposal releases it.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string projectRoot, TimeSpan staleAfter, CancellationToken ct = default);
}
