namespace AdbSync.Core.Transfer;

public interface IAdbTransferEngine
{
    /// <summary>Mirrors the device's remotePath onto the local destination (creates/updates/deletes to match, minus excludes).</summary>
    Task<TransferResult> PullMirrorAsync(string serial, string remotePath, string localPath, IExcludeMatcher exclude, CancellationToken ct = default);

    /// <summary>Mirrors the local source onto the device's remotePath (creates/updates/deletes to match, minus excludes).</summary>
    Task<TransferResult> PushMirrorAsync(string serial, string localPath, string remotePath, IExcludeMatcher exclude, CancellationToken ct = default);
}
