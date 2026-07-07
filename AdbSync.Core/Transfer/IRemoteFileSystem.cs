namespace AdbSync.Core.Transfer;

/// <summary>
/// The seam between the transfer engine and the real ADB sync protocol - isolated so diffing/recursion logic is
/// unit-testable with a fake, while the real implementation (talking to an actual device) is exercised separately.
/// </summary>
public interface IRemoteFileSystem
{
    /// <summary>Lists the immediate children of one directory (not recursive - the sync protocol has no recursive LIST).</summary>
    Task<IReadOnlyList<RemoteFileInfo>> ListDirectoryAsync(string remotePath, CancellationToken ct = default);

    Task PullFileAsync(string remotePath, string localPath, CancellationToken ct = default);

    Task PushFileAsync(string localPath, string remotePath, DateTimeOffset modifiedUtc, CancellationToken ct = default);

    Task DeleteFileAsync(string remotePath, CancellationToken ct = default);

    Task DeleteDirectoryRecursiveAsync(string remotePath, CancellationToken ct = default);

    Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default);
}
