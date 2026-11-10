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
        foreach (var name in plan.JobFolders)
        {
            var source = Path.Combine(oldRoot, name);
            var target = Path.Combine(newRoot, name);
            if (Path.GetPathRoot(Path.GetFullPath(source))!.Equals(
                    Path.GetPathRoot(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(source, target);
                continue;
            }

            CopyDirectory(source, target);
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
