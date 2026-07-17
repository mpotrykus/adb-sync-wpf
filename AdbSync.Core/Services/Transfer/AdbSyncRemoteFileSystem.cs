using AdbSync.Core.Models.Transfer;
using AdbSync.Core.Services.Common;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using System.Text;

namespace AdbSync.Core.Services.Transfer;

/// <summary>
/// Real implementation talking to an actual device: directory listing/pull/push go through the ADB sync
/// protocol (SyncService); delete/mkdir go through "adb shell" since the sync protocol has no delete verb.
/// </summary>
public sealed class AdbSyncRemoteFileSystem : IRemoteFileSystem, IAsyncDisposable
{
    private readonly IAdbClient _adbClient;
    private readonly DeviceData _device;
    private readonly SyncService _sync;
    private readonly TransferPolicy _policy;

    public AdbSyncRemoteFileSystem(IAdbClient adbClient, string serial, TransferPolicy? policy = null)
    {
        _adbClient = adbClient;
        _device = new DeviceData { Serial = serial, State = DeviceState.Online };
        _sync = new SyncService(adbClient, _device);
        _policy = policy ?? TransferPolicy.None;
    }

    public async Task<IReadOnlyList<RemoteFileInfo>> ListDirectoryAsync(string remotePath, CancellationToken ct = default)
    {
        await EnsureOpenAsync(ct);
        var listing = await _sync.GetDirectoryListingAsync(remotePath, ct);
        return listing
            .Where(f => f.Path is not "." and not "..")
            .Select(f => new RemoteFileInfo(
                f.Path,
                (f.FileMode & UnixFileStatus.TypeMask) == UnixFileStatus.Directory,
                f.Size,
                f.Time))
            .ToList();
    }

    public async Task PullFileAsync(string remotePath, string localPath, CancellationToken ct = default)
    {
        await EnsureOpenAsync(ct);
        await RetryHelper.ExecuteAsync(async () =>
        {
            await using var destStream = File.Create(localPath);
            Stream stream = _policy.BandwidthThrottleKBps is > 0
                ? new ThrottledStream(destStream, _policy.BandwidthThrottleKBps.Value * 1024L)
                : destStream;
            await _sync.PullAsync(remotePath, stream, cancellationToken: ct);
        }, _policy.RetryMaxAttempts, _policy.RetryBackoff, ct);
    }

    public async Task PushFileAsync(string localPath, string remotePath, DateTimeOffset modifiedUtc, CancellationToken ct = default)
    {
        await EnsureOpenAsync(ct);
        await RetryHelper.ExecuteAsync(async () =>
        {
            await using var sourceStream = File.OpenRead(localPath);
            Stream stream = _policy.BandwidthThrottleKBps is > 0
                ? new ThrottledStream(sourceStream, _policy.BandwidthThrottleKBps.Value * 1024L)
                : sourceStream;
            await _sync.PushAsync(stream, remotePath, UnixFileStatus.DefaultFileMode, modifiedUtc, cancellationToken: ct);
        }, _policy.RetryMaxAttempts, _policy.RetryBackoff, ct);
    }

    public Task DeleteFileAsync(string remotePath, CancellationToken ct = default) =>
        ExecuteAsync($"rm \"{remotePath}\"", ct);

    public Task DeleteDirectoryRecursiveAsync(string remotePath, CancellationToken ct = default) =>
        ExecuteAsync($"rm -rf \"{remotePath}\"", ct);

    public Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default) =>
        ExecuteAsync($"mkdir -p \"{remotePath}\"", ct);

    private Task ExecuteAsync(string command, CancellationToken ct) =>
        RetryHelper.ExecuteAsync(async () =>
        {
            var receiver = new ConsoleOutputReceiver();
            await _adbClient.ExecuteRemoteCommandAsync(command, _device, receiver, Encoding.UTF8, ct);
        }, _policy.RetryMaxAttempts, _policy.RetryBackoff, ct);

    private async Task EnsureOpenAsync(CancellationToken ct)
    {
        if (!_sync.IsOpen)
            await _sync.OpenAsync(ct);
    }

    public ValueTask DisposeAsync()
    {
        _sync.Dispose();
        return ValueTask.CompletedTask;
    }
}
