using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Common;
using AdbSync.Core.Services.Config;
using System.Globalization;

namespace AdbSync.Core.Services.Orchestration;

/// <summary>Automatic per-sync rollback points under <c>{project}/rollback/{timestamp}/</c>: <c>master/</c> is the
/// master as it was before the sync merged anything, and <c>devices/{name}/</c> is each device's pulled folder
/// before the merge. A point with no <c>devices/</c> folder (taken just before a restore) holds master only.</summary>
public sealed class RollbackPointStore
{
    public const string FolderName = "rollback";
    public const string MasterFolderName = "master";
    public const string DevicesFolderName = "devices";
    private const string TimestampFormat = "yyyy-MM-dd_HH-mm-ss";

    public static string RootFor(string projectRoot) => Path.Combine(projectRoot, FolderName);

    public static bool IsRollbackPoint(string path) =>
        string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))), FolderName, StringComparison.OrdinalIgnoreCase);

    public string Create(string projectRoot)
    {
        var root = RootFor(projectRoot);
        var stamp = DateTimeOffset.Now;
        string path;
        while (Directory.Exists(path = Path.Combine(root, stamp.ToString(TimestampFormat))))
            stamp = stamp.AddSeconds(1);
        Directory.CreateDirectory(path);
        return path;
    }

    public void CaptureMaster(string pointPath, string masterPath)
    {
        var target = Path.Combine(pointPath, MasterFolderName);
        if (Directory.Exists(masterPath))
            DirectoryUtil.CopyRecursive(masterPath, target);
        else
            Directory.CreateDirectory(target);
    }

    public void CaptureDevice(string pointPath, string deviceName, string deviceFolder)
    {
        var target = Path.Combine(pointPath, DevicesFolderName, ConfigNames.Segment(deviceName));
        if (Directory.Exists(deviceFolder))
            DirectoryUtil.CopyRecursive(deviceFolder, target);
        else
            Directory.CreateDirectory(target);
    }

    public void Discard(string pointPath) => DirectoryUtil.DeleteRecursive(pointPath);

    public void Prune(string projectRoot, int keep)
    {
        if (keep <= 0)
            return;

        foreach (var stale in List(projectRoot).Skip(keep))
            DirectoryUtil.DeleteRecursive(stale.Path);
    }

    public IReadOnlyList<SnapshotInfo> List(string projectRoot)
    {
        var root = RootFor(projectRoot);
        if (!Directory.Exists(root))
            return [];

        var points = new List<SnapshotInfo>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!DateTimeOffset.TryParseExact(Path.GetFileName(dir), TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var createdAt))
                continue;

            var devicesDir = Path.Combine(dir, DevicesFolderName);
            var deviceNames = Directory.Exists(devicesDir)
                ? Directory.EnumerateDirectories(devicesDir).Select(Path.GetFileName).OfType<string>()
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            points.Add(new SnapshotInfo(dir, createdAt, deviceNames, SnapshotKind.RollbackPoint));
        }

        return points.OrderByDescending(p => p.CreatedAt).ToList();
    }
}
