namespace AdbSync.Core.Services.Config;

/// <summary>Resolves every on-disk path the app reads/writes, rooted at <see cref="Root"/> so tests can point it elsewhere.</summary>
public sealed class AppPaths(string root, string? defaultProjectsDirectory = null)
{
    public const string DataDirVariable = "ADBSYNC_DATA_DIR";
    public const string ProjectsDirVariable = "ADBSYNC_PROJECTS_DIR";

    public string Root { get; } = root;

    /// <summary>The projects directory used until one is saved in settings.</summary>
    public string DefaultProjectsDirectory { get; } = defaultProjectsDirectory ?? Path.Combine(root, "projects");

    public string ConfigDir => Path.Combine(Root, "config");
    public string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public string DevicesFile => Path.Combine(ConfigDir, "devices.json");
    public string ProjectsFile => Path.Combine(ConfigDir, "projects.json");
    public string ManifestsDir => Path.Combine(Root, "manifests");
    public string PendingConflictsDir => Path.Combine(Root, "pending-conflicts");
    public string LogsDir => Path.Combine(Root, "logs");
    public string CheckpointsDir => Path.Combine(Root, "checkpoints");
    public string RunHistoryDbFile => Path.Combine(Root, "run-history.db");
    public string LocalStateFile => Path.Combine(Root, "local-state.json");
    public string CompanionCertificateFile => Path.Combine(Root, "companion-cert.pfx");

    private static readonly Lazy<AppPaths> DefaultPaths = new(() => FromEnvironment(Environment.GetEnvironmentVariable));

    /// <summary>The paths for this process, from <see cref="FromEnvironment"/>.</summary>
    public static AppPaths Default => DefaultPaths.Value;

    /// <summary>
    /// The data root is <c>ADBSYNC_DATA_DIR</c>, or the per-user app data folder when that's unset. The default projects
    /// directory is <c>ADBSYNC_PROJECTS_DIR</c>, or Documents on Windows and <c>{root}/projects</c> elsewhere, because
    /// Documents on Linux is just $HOME. Both must be full paths: in a container with HOME unset the fallbacks would
    /// otherwise be relative to the working directory.
    /// </summary>
    public static AppPaths FromEnvironment(Func<string, string?> getVariable)
    {
        var root = FullPath(
            NonBlank(getVariable(DataDirVariable))
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbSync"),
            DataDirVariable);

        var projects = NonBlank(getVariable(ProjectsDirVariable)) is { } fromVariable
            ? FullPath(fromVariable, ProjectsDirVariable)
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AdbSync Projects")
                : null;

        return new AppPaths(root, projects);
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FullPath(string path, string variable) =>
        Path.IsPathFullyQualified(path)
            ? path
            : throw new InvalidOperationException($"'{path}' isn't a full path. Set {variable} to a full path.");
}
