using AdbSync.Core.Models.Config;

namespace AdbSync.Core.Services.Config;

/// <summary>Resolves every on-disk path the app reads/writes, rooted at <see cref="Root"/> so tests can point it elsewhere.</summary>
public sealed class AppPaths(string root)
{
    public string Root { get; } = root;
    public string ConfigDir => Path.Combine(Root, "config");
    public string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public string DevicesFile => Path.Combine(ConfigDir, "devices.json");
    public string ProjectsFile => Path.Combine(ConfigDir, "projects.json");
    public string ManifestsDir => Path.Combine(Root, "manifests");
    public string LogsDir => Path.Combine(Root, "logs");
    public string CheckpointFile => Path.Combine(Root, ".sync_checkpoint.json");
    public string RunHistoryDbFile => Path.Combine(Root, "run-history.db");

    public static AppPaths Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbSync"));
}
