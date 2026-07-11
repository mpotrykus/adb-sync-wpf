namespace AdbSync.Core.Models.Config;

public sealed class SyncJobConfig
{
    /// <summary>Folder name under <see cref="GlobalSettings.ProjectsDirectory"/> (or <see cref="ProjectDirectory"/> if set).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional override of <see cref="GlobalSettings.ProjectsDirectory"/> for this job only.</summary>
    public string? ProjectDirectory { get; set; }

    /// <summary>Android package id. If set, the whole job is skipped while running on any bound device.</summary>
    public string? AppPackage { get; set; }

    /// <summary>Name/relative-path patterns excluded from both pull and push.</summary>
    public List<string> Exclude { get; set; } = [];

    public List<JobDeviceBinding> Devices { get; set; } = [];
    public JobSchedule Schedule { get; set; } = new();
    public bool Enabled { get; set; } = true;

    /// <summary>Optional override of <see cref="GlobalSettings.StaleLockHours"/> for this job only.</summary>
    public int? StaleLockHours { get; set; }

    /// <summary>Optional override of <see cref="GlobalSettings.ConflictRetentionDays"/> for this job only.</summary>
    public int? ConflictRetentionDays { get; set; }

    /// <summary>Optional override of <see cref="GlobalSettings.ShowInfoNotifications"/> for this job only.</summary>
    public bool? ShowInfoNotifications { get; set; }

    /// <summary>Optional override of <see cref="GlobalSettings.ShowErrorNotifications"/> for this job only.</summary>
    public bool? ShowErrorNotifications { get; set; }

    /// <summary>Optional override of <see cref="GlobalSettings.MaxRunHistoryEntries"/> for this job only.</summary>
    public int? MaxRunHistoryEntries { get; set; }

    /// <summary>Optional override of <see cref="GlobalSettings.BackupConflictLosers"/> for this job only.</summary>
    public bool? BackupConflictLosers { get; set; }

    /// <summary>Optional override of <see cref="GlobalSettings.CheckpointRetentionDays"/> for this job only.</summary>
    public int? CheckpointRetentionDays { get; set; }

    /// <summary>Optional override of <see cref="GlobalSettings.BandwidthThrottleKBps"/> for this job only.</summary>
    public int? BandwidthThrottleKBps { get; set; }

    /// <summary>Optional override of <see cref="GlobalSettings.RetryMaxAttempts"/> for this job only.</summary>
    public int? RetryMaxAttempts { get; set; }

    /// <summary>Optional override of <see cref="GlobalSettings.RetryBackoffSeconds"/> for this job only.</summary>
    public int? RetryBackoffSeconds { get; set; }

    /// <summary>Optional override of <see cref="GlobalSettings.PushSafetyMinimumPercent"/> for this job only.</summary>
    public int? PushSafetyMinimumPercent { get; set; }

    /// <summary>When true, this job always runs as a rehearsal - master/manifest/checkpoints are never written and push never runs. Job-only, no global fallback.</summary>
    public bool DryRun { get; set; }
}
