namespace AdbSync.Core.Services.Orchestration;

public interface ISyncLockManager
{
    /// <summary>Returns null if another run holds the lock; otherwise an acquired handle whose disposal releases it.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string projectRoot, CancellationToken ct = default);
}
