namespace AdbSync.Core.Models.Config;

public sealed class GlobalSettings
{
    public static string DefaultProjectsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AdbSync Projects");

    public string ProjectsDirectory { get; set; } = DefaultProjectsDirectory;
    public bool StartAtLogin { get; set; } = true;
    public bool ShowInfoNotifications { get; set; }
    public bool ShowErrorNotifications { get; set; } = true;
    public int StaleLockHours { get; set; } = 4;
    public int LogRetentionDays { get; set; } = 30;
    public int ConflictRetentionDays { get; set; } = 30;
    public long PerLogFileMaxBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxConcurrentJobs { get; set; } = 1;
}
