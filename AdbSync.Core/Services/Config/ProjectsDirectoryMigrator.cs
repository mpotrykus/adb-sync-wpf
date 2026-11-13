using AdbSync.Core.Services.Common;
using AdbSync.Core.Models.Config;

namespace AdbSync.Core.Services.Config;

public sealed record ProjectsMigrationPlan(IReadOnlyList<string> JobFolders, IReadOnlyList<string> Conflicts);

public static class ProjectsDirectoryMigrator
{
    public static ProjectsMigrationPlan Plan(IEnumerable<SyncJobConfig> jobs, string oldRoot, string newRoot)
    {
        var folders = new List<string>();
        var conflicts = new List<string>();
        if (string.IsNullOrWhiteSpace(oldRoot) || !Directory.Exists(oldRoot))
            return new ProjectsMigrationPlan(folders, conflicts);

        foreach (var job in jobs.Where(j => string.IsNullOrWhiteSpace(j.ProjectDirectory)))
        {
            if (!Directory.Exists(Path.Combine(oldRoot, job.Name)))
                continue;
            folders.Add(job.Name);
            if (Directory.Exists(Path.Combine(newRoot, job.Name)))
                conflicts.Add(job.Name);
        }
        return new ProjectsMigrationPlan(folders, conflicts);
    }

    public static bool IsSameOrNested(string oldRoot, string newRoot)
    {
        var a = Path.GetFullPath(oldRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var b = Path.GetFullPath(newRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return b.StartsWith(a, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    public static void Move(ProjectsMigrationPlan plan, string oldRoot, string newRoot)
    {
        Directory.CreateDirectory(newRoot);
        // The moved jobs' manifests record the old root's id, so the id travels with the data instead of the new
        // root getting a fresh one that would make those jobs refuse to run.
        var oldMarker = Path.Combine(oldRoot, ProjectFolders.MarkerFileName);
        var newMarker = Path.Combine(newRoot, ProjectFolders.MarkerFileName);
        if (File.Exists(oldMarker) && !File.Exists(newMarker))
            File.Copy(oldMarker, newMarker);
        foreach (var name in plan.JobFolders)
        {
            var source = Path.Combine(oldRoot, name);
            var target = Path.Combine(newRoot, name);
            try
            {
                Directory.Move(source, target);
                continue;
            }
            catch (IOException) when (Directory.Exists(source) && !Directory.Exists(target))
            {
                // Across drives or mounts a move isn't possible (EXDEV on Linux, where every path shares the root "/").
            }

            // Copied under a temporary name and renamed once complete, so an interrupted copy never leaves a
            // half-filled folder under the job's name; the source is only deleted after that.
            var partial = $"{target}.partial-{Guid.NewGuid():N}";
            try
            {
                CopyDirectory(source, partial);
                Directory.Move(partial, target);
            }
            catch
            {
                if (Directory.Exists(partial))
                    DirectoryUtil.DeleteRecursive(partial);
                throw;
            }
            DirectoryUtil.DeleteRecursive(source);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}
