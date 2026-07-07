namespace AdbSync.Core.Config;

public sealed class GlobalSettings
{
    public string ProjectsDirectory { get; set; } = string.Empty;
    public bool StartAtLogin { get; set; } = true;
    public bool ShowInfoNotifications { get; set; }
    public bool ShowErrorNotifications { get; set; } = true;
    public int StaleLockHours { get; set; } = 4;
    public int LogRetentionDays { get; set; } = 30;
    public long PerLogFileMaxBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxConcurrentJobs { get; set; } = 1;
}
