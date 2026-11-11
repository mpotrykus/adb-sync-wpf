using AdbSync.Core.Models.Config;

namespace AdbSync.Core.Services.Config;

public static class ProjectFolders
{
    /// <summary>Why <paramref name="root"/> can't be used as a projects directory, or null when it can.</summary>
    public static string? RootProblem(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return "Projects directory is not configured - set it in Settings before running a job.";
        if (!Path.IsPathFullyQualified(root))
            return $"Projects directory '{root}' isn't a full path on this system.";
        return null;
    }

    /// <summary>The job's project folder (<c>{projects directory}/{job name}</c>), or throws when the root or name is invalid.</summary>
    public static string ProjectRoot(EffectiveJobSettings eff, string jobName) =>
        RootProblem(eff.ProjectsDirectory) is { } problem
            ? throw new InvalidOperationException(problem)
            : Path.Combine(eff.ProjectsDirectory, ConfigNames.Segment(jobName));

    /// <summary>The job's project folder, or null when the root or name is invalid. For read-only lookups.</summary>
    public static string? TryProjectRoot(EffectiveJobSettings eff, string jobName) =>
        RootProblem(eff.ProjectsDirectory) is null && ConfigNames.Problem(jobName) is null
            ? Path.Combine(eff.ProjectsDirectory, jobName)
            : null;
}
