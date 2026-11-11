using AdbSync.Core.Models.Config;
using System.Text.Json;

namespace AdbSync.Core.Services.Config;

/// <summary>
/// Resolves a job's project folder and decides whether its root (the projects directory, or the job's override) can be
/// used. A root is only usable when it exists and carries a <see cref="MarkerFileName"/> marker: an unmounted drive
/// leaves an empty mount point behind, and treating that as the root would turn every master into an empty one. The
/// root itself is never created by a run, only when the user chooses it (<see cref="AdoptRoot"/>).
/// </summary>
public static class ProjectFolders
{
    public const string MarkerFileName = ".adbsync-root";

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

    /// <summary>
    /// Why the job's root can't be used right now, or null when it can. A root without a marker that already holds this
    /// job's folder is adopted (the marker is written), which is how roots from before the marker existed get one.
    /// </summary>
    public static string? Unavailable(EffectiveJobSettings eff, string jobName)
    {
        if (RootProblem(eff.ProjectsDirectory) is { } problem)
            return problem;

        var root = eff.ProjectsDirectory;
        if (!Directory.Exists(root))
            return $"Projects folder '{root}' isn't reachable.";

        var markerPath = Path.Combine(root, MarkerFileName);
        try
        {
            if (!File.Exists(markerPath))
            {
                if (!Directory.Exists(Path.Combine(root, ConfigNames.Segment(jobName))))
                    return $"Projects folder '{root}' has no {MarkerFileName} marker, so it may be an empty mount point. " +
                           "If it's the right folder, choose it again in Settings.";
                WriteMarker(markerPath);
            }

            using var marker = new FileStream(markerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            JsonSerializer.Deserialize(marker, AppConfigJsonContext.Default.ProjectsRootMarker);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return $"Projects folder '{root}' isn't readable and writable: {ex.Message}";
        }
    }

    /// <summary>The job's project folder, or throws when its root can't be used right now.</summary>
    public static string RequireAvailable(EffectiveJobSettings eff, string jobName)
    {
        var projectRoot = ProjectRoot(eff, jobName);
        return Unavailable(eff, jobName) is { } reason ? throw new ProjectFolderUnavailableException(reason) : projectRoot;
    }

    /// <summary>
    /// Throws when the root's marker or the job's master has disappeared since the run started, for example because the
    /// drive dropped out. Without this, a missing master would be read as every file having been deleted.
    /// </summary>
    public static void EnsureStillAvailable(EffectiveJobSettings eff, string masterPath)
    {
        if (!File.Exists(Path.Combine(eff.ProjectsDirectory, MarkerFileName)) || !Directory.Exists(masterPath))
            throw new ProjectFolderUnavailableException($"Projects folder '{eff.ProjectsDirectory}' became unavailable during the run.");
    }

    /// <summary>Makes <paramref name="root"/> a projects root because the user chose it: creates it and its marker if missing.</summary>
    public static void AdoptRoot(string root)
    {
        if (RootProblem(root) is { } problem)
            throw new InvalidOperationException(problem);
        Directory.CreateDirectory(root);
        var markerPath = Path.Combine(root, MarkerFileName);
        if (!File.Exists(markerPath))
            WriteMarker(markerPath);
    }

    private static void WriteMarker(string markerPath)
    {
        var marker = new ProjectsRootMarker { RootId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
        File.WriteAllText(markerPath, JsonSerializer.Serialize(marker, AppConfigJsonContext.Default.ProjectsRootMarker));
        if (OperatingSystem.IsWindows())
            File.SetAttributes(markerPath, File.GetAttributes(markerPath) | FileAttributes.Hidden);
    }
}

public sealed class ProjectsRootMarker
{
    public Guid RootId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ProjectFolderUnavailableException(string message) : IOException(message);
