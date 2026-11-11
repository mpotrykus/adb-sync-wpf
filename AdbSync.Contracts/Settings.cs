namespace AdbSync.Contracts;

public sealed record QuietHoursDto(bool Enabled, string Start, string End);

public sealed record NotificationSettingsDto(bool ShowInfo, bool ShowErrors, QuietHoursDto QuietHours);

public sealed record SettingsDto(
    string ProjectsDirectory, string TimeZone, int LogRetentionDays, int ConflictRetentionDays, long PerLogFileMaxBytes,
    int MaxConcurrentJobs, int MaxConcurrentPerDevice, NotificationSettingsDto Notifications, int CheckpointRetentionDays,
    int RollbackPointsToKeep, int? BandwidthThrottleKBps, int RetryMaxAttempts, int RetryBackoffSeconds, int PushSafetyMinimumPercent,
    int MaxRunHistoryEntries, int FileDetailRunsKept, string ConflictPolicy, bool BackupConflictLosers, bool RunMissedSchedules,
    bool CompanionBroadcastsEnabled);

public sealed record DashboardPreferencesDto(string? SortKey, bool Descending);

public sealed record MoveProjectsRequest(string? Path, bool MoveExisting);

public sealed record PlannedMove(Guid JobId, string From, string To, long? SizeBytes, bool CrossDevice);

public sealed record PlanBlocker(string Code, Guid? JobId, string Detail);

public sealed record ProjectsDirectoryPlanDto(string Path, bool Reachable, IReadOnlyList<PlannedMove> Moves, IReadOnlyList<PlanBlocker> Blockers);
