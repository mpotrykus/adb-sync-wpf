using AdbSync.Core.Models.Transfer;

namespace AdbSync.Core.Services.Transfer;

/// <summary>
/// V2 transfer engine: talks the real ADB sync protocol directly (via <see cref="IRemoteFileSystem"/>) instead
/// of shelling out to adb.exe. Unlike <see cref="AdbExeTransferEngine"/>, this gets genuine per-file diffing on
/// BOTH pull and push (v1 only diffed pull; push was always a brute-force full re-upload).
/// </summary>
public sealed class NativeAdbTransferEngine(IRemoteFileSystemFactory remoteFactory, IMirrorDiffer differ) : IAdbTransferEngine
{
    public async Task<TransferResult> PullMirrorAsync(string serial, string remotePath, string localPath, IExcludeMatcher exclude, TransferPolicy? policy = null, CancellationToken ct = default)
    {
        var remote = remoteFactory.Create(serial, policy);
        var remoteEntries = await RemoteFileTreeScanner.ScanAsync(remote, remotePath, exclude, ct);

        Directory.CreateDirectory(localPath);
        var localEntries = LocalFileTreeScanner.Scan(localPath, exclude);

        var plan = differ.Diff(remoteEntries, localEntries);
        var errors = new List<string>();

        foreach (var dirEntry in plan.ToCopy.Where(e => e.IsDirectory))
            Directory.CreateDirectory(Path.Combine(localPath, dirEntry.RelativePath));

        var copied = 0;
        var bytesCopied = 0L;
        var copiedPaths = new List<string>();
        foreach (var entry in plan.ToCopy.Where(e => !e.IsDirectory))
        {
            try
            {
                var destPath = Path.Combine(localPath, entry.RelativePath);
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                var tempPath = $"{destPath}.tmp-{Guid.NewGuid():N}";
                await remote.PullFileAsync($"{remotePath}/{entry.RelativePath}", tempPath, ct);
                File.SetLastWriteTimeUtc(tempPath, entry.ModifiedUtc.UtcDateTime);
                File.Move(tempPath, destPath, overwrite: true);
                copied++;
                copiedPaths.Add(entry.RelativePath);
                bytesCopied += entry.Size;
            }
            catch (Exception ex)
            {
                errors.Add($"pull '{entry.RelativePath}' failed: {ex.Message}");
            }
        }

        var deleted = 0;
        var deletedPaths = new List<string>();
        foreach (var entry in plan.ToDelete)
        {
            var path = Path.Combine(localPath, entry.RelativePath);
            if (entry.IsDirectory)
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
            deleted++;
            deletedPaths.Add(entry.RelativePath);
        }

        return new TransferResult(copied, deleted, bytesCopied, errors, copiedPaths, deletedPaths);
    }

    public async Task<TransferResult> PushMirrorAsync(string serial, string localPath, string remotePath, IExcludeMatcher exclude, TransferPolicy? policy = null, CancellationToken ct = default)
    {
        var remote = remoteFactory.Create(serial, policy);
        await remote.CreateDirectoryAsync(remotePath, ct);

        Directory.CreateDirectory(localPath);
        var localEntries = LocalFileTreeScanner.Scan(localPath, exclude);
        var remoteEntries = await RemoteFileTreeScanner.ScanAsync(remote, remotePath, exclude, ct);

        var plan = differ.Diff(localEntries, remoteEntries);
        var errors = new List<string>();

        foreach (var dirEntry in plan.ToCopy.Where(e => e.IsDirectory).OrderBy(e => e.RelativePath.Count(c => c == '/')))
        {
            try
            {
                await remote.CreateDirectoryAsync($"{remotePath}/{dirEntry.RelativePath}", ct);
            }
            catch (Exception ex)
            {
                errors.Add($"mkdir '{dirEntry.RelativePath}' failed: {ex.Message}");
            }
        }

        var copied = 0;
        var bytesCopied = 0L;
        var copiedPaths = new List<string>();
        foreach (var entry in plan.ToCopy.Where(e => !e.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await remote.PushFileAsync(Path.Combine(localPath, entry.RelativePath), $"{remotePath}/{entry.RelativePath}", entry.ModifiedUtc, CancellationToken.None);
                copied++;
                copiedPaths.Add(entry.RelativePath);
                bytesCopied += entry.Size;
            }
            catch (Exception ex)
            {
                errors.Add($"push '{entry.RelativePath}' failed: {ex.Message}");
            }
        }

        var deleted = 0;
        var deletedPaths = new List<string>();
        foreach (var entry in plan.ToDelete)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (entry.IsDirectory)
                    await remote.DeleteDirectoryRecursiveAsync($"{remotePath}/{entry.RelativePath}", CancellationToken.None);
                else
                    await remote.DeleteFileAsync($"{remotePath}/{entry.RelativePath}", CancellationToken.None);
                deleted++;
                deletedPaths.Add(entry.RelativePath);
            }
            catch (Exception ex)
            {
                errors.Add($"delete '{entry.RelativePath}' failed: {ex.Message}");
            }
        }

        return new TransferResult(copied, deleted, bytesCopied, errors, copiedPaths, deletedPaths);
    }
}
