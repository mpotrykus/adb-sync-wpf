namespace AdbSync.Core.Transfer;

public static class MirrorPlanApplier
{
    /// <summary>Applies a plan between two local directory trees. ToCopy paths are read from <paramref name="sourceRoot"/>; both are applied against <paramref name="destRoot"/>.</summary>
    public static (int Copied, int Deleted, long BytesCopied) Apply(MirrorPlan plan, string sourceRoot, string destRoot)
    {
        var copied = 0;
        var bytesCopied = 0L;
        foreach (var entry in plan.ToCopy)
        {
            var destPath = Path.Combine(destRoot, entry.RelativePath);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destPath);
                continue;
            }

            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            var sourcePath = Path.Combine(sourceRoot, entry.RelativePath);
            var tempPath = $"{destPath}.tmp-{Guid.NewGuid():N}";
            File.Copy(sourcePath, tempPath, overwrite: true);
            File.SetLastWriteTimeUtc(tempPath, entry.ModifiedUtc.UtcDateTime);
            File.Move(tempPath, destPath, overwrite: true);
            copied++;
            bytesCopied += entry.Size;
        }

        var deleted = 0;
        foreach (var entry in plan.ToDelete)
        {
            var path = Path.Combine(destRoot, entry.RelativePath);
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
        }

        return (copied, deleted, bytesCopied);
    }
}
