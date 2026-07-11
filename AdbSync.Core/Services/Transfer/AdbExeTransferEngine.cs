using AdbSync.Core.Models.Transfer;

namespace AdbSync.Core.Services.Transfer;

/// <summary>
/// V1 transfer engine: shells out to real adb.exe for the actual data movement, but does all diffing,
/// exclude filtering, and mirror-delete decisions locally against plain filesystem/text output. A stepping
/// stone ahead of the native ADB-sync-protocol engine (Phase 6) - see the project plan's risk/phasing section.
/// </summary>
public sealed class AdbExeTransferEngine(IAdbProcessRunner adb, IMirrorDiffer differ) : IAdbTransferEngine
{
    public async Task<TransferResult> PullMirrorAsync(string serial, string remotePath, string localPath, IExcludeMatcher exclude, CancellationToken ct = default)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AdbSync", $"pull-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var pullResult = await adb.RunAsync(["-s", serial, "pull", remotePath, tempRoot], ct);
            if (pullResult.ExitCode != 0)
                return new TransferResult(0, 0, 0, [$"adb pull failed (exit {pullResult.ExitCode}): {pullResult.StandardError}"], [], []);

            var pulledRoot = Path.Combine(tempRoot, Path.GetFileName(remotePath.TrimEnd('/')));
            Directory.CreateDirectory(localPath);

            var source = LocalFileTreeScanner.Scan(pulledRoot, exclude);
            var destination = LocalFileTreeScanner.Scan(localPath, exclude);
            var plan = differ.Diff(source, destination);
            var (copied, deleted, bytesCopied) = MirrorPlanApplier.Apply(plan, pulledRoot, localPath);
            var copiedPaths = plan.ToCopy.Where(e => !e.IsDirectory).Select(e => e.RelativePath).ToList();
            var deletedPaths = plan.ToDelete.Select(e => e.RelativePath).ToList();

            return new TransferResult(copied, deleted, bytesCopied, [], copiedPaths, deletedPaths);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    public async Task<TransferResult> PushMirrorAsync(string serial, string localPath, string remotePath, IExcludeMatcher exclude, CancellationToken ct = default)
    {
        var errors = new List<string>();

        var mkdirResult = await adb.RunAsync(["-s", serial, "shell", "mkdir", "-p", remotePath], ct);
        if (mkdirResult.ExitCode != 0)
            errors.Add($"mkdir -p failed (exit {mkdirResult.ExitCode}): {mkdirResult.StandardError}");

        var local = LocalFileTreeScanner.Scan(localPath, exclude);

        var pushed = 0;
        var bytesCopied = 0L;
        var copiedPaths = new List<string>();
        if (Directory.Exists(localPath))
        {
            foreach (var childPath in Directory.EnumerateFileSystemEntries(localPath))
            {
                var childName = Path.GetFileName(childPath);
                var isDirectory = Directory.Exists(childPath);
                if (exclude.IsExcluded(childName, isDirectory))
                    continue;

                var pushResult = await adb.RunAsync(["-s", serial, "push", childPath, $"{remotePath}/{childName}"], ct);
                if (pushResult.ExitCode != 0)
                {
                    errors.Add($"push '{childName}' failed (exit {pushResult.ExitCode}): {pushResult.StandardError}");
                }
                else
                {
                    pushed++;
                    copiedPaths.Add(childName);
                    bytesCopied += GetLocalSize(childPath, isDirectory);
                }
            }
        }

        var (deleted, deletedPaths) = await DeleteRemoteExtrasAsync(serial, remotePath, local, exclude, errors, ct);

        return new TransferResult(pushed, deleted, bytesCopied, errors, copiedPaths, deletedPaths);
    }

    // adb push sends the whole raw subtree for a directory child (unfiltered by nested excludes), so this
    // mirrors that by summing every file under it rather than relying on the already-exclude-filtered scan.
    private static long GetLocalSize(string path, bool isDirectory) => isDirectory
        ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
        : new FileInfo(path).Length;

    private async Task<(int Deleted, List<string> DeletedPaths)> DeleteRemoteExtrasAsync(
        string serial, string remotePath, List<FileEntry> local, IExcludeMatcher exclude, List<string> errors, CancellationToken ct)
    {
        var remoteDirs = await ListRemoteAsync(serial, remotePath, isDirectory: true, ct);
        var remoteFiles = await ListRemoteAsync(serial, remotePath, isDirectory: false, ct);
        var localPaths = new HashSet<string>(local.Select(e => e.RelativePath), StringComparer.Ordinal);

        var extraDirs = remoteDirs
            .Where(p => !localPaths.Contains(p) && !exclude.IsExcluded(p, true))
            .ToHashSet(StringComparer.Ordinal);
        var topMostExtraDirs = extraDirs.Where(p => !RelativePathUtil.HasAncestorIn(p, extraDirs)).ToList();

        var extraFiles = remoteFiles
            .Where(p => !localPaths.Contains(p) && !exclude.IsExcluded(p, false))
            .Where(p => !topMostExtraDirs.Any(d => p.StartsWith(d + "/", StringComparison.Ordinal)))
            .ToList();

        var deleted = 0;
        var deletedPaths = new List<string>();
        foreach (var dir in topMostExtraDirs)
        {
            var result = await adb.RunAsync(["-s", serial, "shell", "rm", "-rf", $"{remotePath}/{dir}"], ct);
            if (result.ExitCode != 0)
                errors.Add($"rm -rf '{dir}' failed (exit {result.ExitCode}): {result.StandardError}");
            else
            {
                deleted++;
                deletedPaths.Add(dir);
            }
        }
        foreach (var file in extraFiles)
        {
            var result = await adb.RunAsync(["-s", serial, "shell", "rm", $"{remotePath}/{file}"], ct);
            if (result.ExitCode != 0)
                errors.Add($"rm '{file}' failed (exit {result.ExitCode}): {result.StandardError}");
            else
            {
                deleted++;
                deletedPaths.Add(file);
            }
        }

        return (deleted, deletedPaths);
    }

    private async Task<List<string>> ListRemoteAsync(string serial, string remotePath, bool isDirectory, CancellationToken ct)
    {
        var type = isDirectory ? "d" : "f";
        var result = await adb.RunAsync(["-s", serial, "shell", "find", remotePath, "-mindepth", "1", "-type", type], ct);
        if (result.ExitCode != 0)
            return [];

        var prefix = remotePath.TrimEnd('/') + "/";
        return result.StandardOutput
            .Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0 && l.StartsWith(prefix, StringComparison.Ordinal))
            .Select(l => l[prefix.Length..])
            .ToList();
    }
}
