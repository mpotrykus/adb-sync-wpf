using AdbSync.Core.Models.Transfer;
using AdbSync.Core.Services.Transfer;

namespace AdbSync.Core.Tests.Orchestration.Fakes;

/// <summary>
/// Simulates "the device" as an ordinary local folder (keyed by serial) and reuses the real, already-tested
/// Transfer components to mirror between it and staging/master. Lets orchestration tests exercise the full
/// pull/merge/push pipeline end-to-end without touching adb.exe.
/// </summary>
public sealed class FakeAdbTransferEngine(IReadOnlyDictionary<string, string> deviceFolders) : IAdbTransferEngine
{
    private readonly MirrorDiffer _differ = new();

    public int FilesPulledTotal { get; private set; }

    public Task<TransferResult> PullMirrorAsync(string serial, string remotePath, string localPath, IExcludeMatcher exclude, TransferPolicy? policy = null, CancellationToken ct = default)
    {
        var result = Mirror(deviceFolders[serial], localPath, exclude, policy?.SkipPull);
        FilesPulledTotal += result.CopiedPaths.Count;
        return Task.FromResult(result);
    }

    public Task<TransferResult> PushMirrorAsync(string serial, string localPath, string remotePath, IExcludeMatcher exclude, TransferPolicy? policy = null, CancellationToken ct = default) =>
        Task.FromResult(Mirror(localPath, deviceFolders[serial], exclude, null));

    private TransferResult Mirror(string sourceRoot, string destRoot, IExcludeMatcher exclude, Func<FileEntry, bool>? skip)
    {
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destRoot);
        var source = LocalFileTreeScanner.Scan(sourceRoot, exclude);
        List<FileEntry>? skipped = null;
        if (skip is not null)
        {
            skipped = source.Where(e => !e.IsDirectory && skip(e)).ToList();
            var skippedPaths = skipped.Select(e => e.RelativePath).ToHashSet(StringComparer.Ordinal);
            source = source.Where(e => !skippedPaths.Contains(e.RelativePath)).ToList();
        }
        var destination = LocalFileTreeScanner.Scan(destRoot, exclude);
        var plan = _differ.Diff(source, destination);
        var (copied, deleted, bytesCopied) = MirrorPlanApplier.Apply(plan, sourceRoot, destRoot);
        var copiedPaths = plan.ToCopy.Where(e => !e.IsDirectory).Select(e => e.RelativePath).ToList();
        var deletedPaths = plan.ToDelete.Select(e => e.RelativePath).ToList();
        return new TransferResult(copied, deleted, bytesCopied, [], copiedPaths, deletedPaths, null, skipped);
    }
}
