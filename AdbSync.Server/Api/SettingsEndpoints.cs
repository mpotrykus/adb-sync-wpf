using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Scheduling;
using AdbSync.Engine.Services;
using AdbSync.Server.Events;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdbSync.Server.Api;

public static class SettingsEndpoints
{
    public const string BrowseRootsVariable = "ADBSYNC_BROWSE_ROOTS";
    private static readonly string[] SortKeys = ["order", "name", "lastRun", "nextRun", "status"];

    public static SettingsDto ToDto(GlobalSettings s) => new(
        s.ProjectsDirectory, ScheduleClock.Zone(s).Id, s.LogRetentionDays, s.ConflictRetentionDays, s.PerLogFileMaxBytes,
        s.MaxConcurrentJobs, s.MaxConcurrentPerDevice,
        new NotificationSettingsDto(s.ShowInfoNotifications, s.ShowErrorNotifications,
            new QuietHoursDto(s.QuietHoursEnabled, s.QuietHoursStart.ToString("HH:mm", CultureInfo.InvariantCulture), s.QuietHoursEnd.ToString("HH:mm", CultureInfo.InvariantCulture))),
        s.CheckpointRetentionDays, s.RollbackPointsToKeep, s.BandwidthThrottleKBps, s.RetryMaxAttempts, s.RetryBackoffSeconds,
        s.PushSafetyMinimumPercent, s.MaxRunHistoryEntries, s.FileDetailRunsKept,
        s.ConflictPolicy == ConflictPolicy.Ask ? "ask" : "newestWins", s.BackupConflictLosers, s.RunMissedSchedules, s.CompanionBroadcastsEnabled,
        s.IncrementalPull, s.FullPullIntervalDays);

    public static void MapSettingsEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/settings", async (AppConfigService configService) => ToDto((await configService.GetAsync()).Settings));

        api.MapPatch("/settings", async (JsonObject patch, ConfigEditor editor, AppConfigService configService, EventBus bus) =>
            await editor.EditAsync(async config =>
            {
                if (patch.ContainsKey("projectsDirectory"))
                    return Problems.Validation("projectsDirectory is changed through POST /settings/projects-directory.");

                var merged = JsonSerializer.SerializeToNode(ToDto(config.Settings), JobMapping.Json)!.AsObject();
                Merge(merged, patch);
                SettingsDto input;
                try
                {
                    input = merged.Deserialize<SettingsDto>(JobMapping.Json)!;
                }
                catch (JsonException ex)
                {
                    return Problems.Validation(ex.Message);
                }
                if (Validate(input) is { Count: > 0 } errors)
                    return Results.Problem(title: "The settings aren't valid.", statusCode: StatusCodes.Status400BadRequest,
                        type: "https://adbsync.dev/problems/validation_failed",
                        extensions: new Dictionary<string, object?> { ["code"] = "validation_failed", ["errors"] = errors });

                Apply(input, config.Settings);
                await configService.SaveAsync();
                var dto = ToDto(config.Settings);
                bus.Publish("settings.changed", dto);
                return Results.Ok(dto);
            })).Accepts<JsonObject>("application/merge-patch+json", "application/json");

        api.MapGet("/preferences/dashboard", async (DashboardUiStateStore store, CancellationToken ct) =>
        {
            var state = await store.LoadAsync(ct);
            return new DashboardPreferencesDto(SortKeys.Contains(state.SortColumn) ? state.SortColumn : "order", state.SortDescending);
        });

        api.MapPut("/preferences/dashboard", async (DashboardPreferencesDto prefs, DashboardUiStateStore store, EventBus bus, CancellationToken ct) =>
        {
            if (prefs.SortKey is not null && !SortKeys.Contains(prefs.SortKey))
                return Problems.Validation($"sortKey must be one of: {string.Join(", ", SortKeys)}.");
            await store.SaveAsync(new DashboardUiState { SortColumn = prefs.SortKey ?? "order", SortDescending = prefs.Descending }, ct);
            var saved = new DashboardPreferencesDto(prefs.SortKey ?? "order", prefs.Descending);
            bus.Publish("preferences.changed", saved);
            return Results.Ok(saved);
        });

        api.MapGet("/settings/projects-directory/plan", async (string path, AppConfigService configService, JobRunService runs) =>
            Results.Ok(Plan(path, await configService.GetAsync(), runs)));

        api.MapPost("/settings/projects-directory", async (
            MoveProjectsRequest request, ConfigEditor editor, AppConfigService configService, JobRunService runs, OperationRegistry operations, EventBus bus) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path) || !Path.IsPathFullyQualified(request.Path))
                return Problems.Validation("path must be a full path on the server.");
            var config = await configService.GetAsync();
            var plan = Plan(request.Path, config, runs);
            if (plan.Blockers.FirstOrDefault(b => request.MoveExisting || b.Code == "jobs_running") is { } blocker)
                return Problems.Create(StatusCodes.Status409Conflict, blocker.Code, "The projects directory can't be changed right now.", blocker.Detail);

            var operation = operations.Start("changeProjectsDirectory", null, async (progress, _) =>
            {
                using var hold = runs.HoldNewRuns("the projects directory is being changed");
                if (runs.AnyRunning)
                    throw new OperationFailedException("jobs_running", "A job started running; try again when it finishes.");
                await editor.EditAsync(async current =>
                {
                    var oldRoot = current.Settings.ProjectsDirectory;
                    var newRoot = request.Path!.Trim();
                    if (request.MoveExisting)
                    {
                        var migration = ProjectsDirectoryMigrator.Plan(current.Jobs, oldRoot, newRoot);
                        progress($"Moving {migration.JobFolders.Count} project folder(s)", null);
                        ProjectsDirectoryMigrator.Move(migration, oldRoot, newRoot);
                    }
                    ProjectFolders.AdoptRoot(newRoot);
                    current.Settings.ProjectsDirectory = newRoot;
                    await configService.SaveAsync();
                    bus.Publish("settings.changed", ToDto(current.Settings));
                    return 0;
                });
                return null;
            });
            return Results.Accepted($"/api/v1/operations/{operation.Id}", operation);
        });

        api.MapGet("/server-fs/browse", async (string? path, AppConfigService configService) => Browse(path, await configService.GetAsync()));

        api.MapGet("/operations", (OperationRegistry operations) => operations.List());

        api.MapGet("/operations/{operationId}", (string operationId, OperationRegistry operations) =>
            operations.Get(operationId) is { } operation ? Results.Ok(operation) : Problems.NotFound("No operation with that id."));
    }

    private static ProjectsDirectoryPlanDto Plan(string path, AppConfig config, JobRunService runs)
    {
        var blockers = new List<PlanBlocker>();
        var moves = new List<PlannedMove>();
        if (!Path.IsPathFullyQualified(path))
            return new ProjectsDirectoryPlanDto(path, false, moves, [new PlanBlocker("validation_failed", null, "Not a full path on the server.")]);

        var oldRoot = config.Settings.ProjectsDirectory;
        var reachable = Directory.Exists(path) || (Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent));
        if (runs.AnyRunning)
            blockers.Add(new PlanBlocker("jobs_running", null, "Wait for running jobs to finish."));
        if (Directory.Exists(oldRoot) && ProjectsDirectoryMigrator.IsSameOrNested(oldRoot, path))
            blockers.Add(new PlanBlocker("validation_failed", null, "The new directory can't be inside the old one, or the other way round."));
        else
        {
            var migration = ProjectsDirectoryMigrator.Plan(config.Jobs, oldRoot, path);
            foreach (var name in migration.JobFolders)
            {
                var job = config.Jobs.First(j => j.Name == name);
                var from = Path.Combine(oldRoot, name);
                var to = Path.Combine(path, name);
                moves.Add(new PlannedMove(job.Id, from, to, FolderSize(from), !string.Equals(MountOf(from), MountOf(path), StringComparison.Ordinal)));
                if (migration.Conflicts.Contains(name))
                    blockers.Add(new PlanBlocker("folder_exists", job.Id, $"'{to}' already exists."));
            }
        }
        return new ProjectsDirectoryPlanDto(path, reachable, moves, blockers);
    }

    private static IResult Browse(string? path, AppConfig config)
    {
        var roots = new List<string> { config.Settings.ProjectsDirectory };
        roots.AddRange(config.Jobs.Select(j => j.ProjectDirectory).OfType<string>());
        roots.AddRange((Environment.GetEnvironmentVariable(BrowseRootsVariable) ?? "")
            .Split([';', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathFullyQualified));
        roots = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Path.GetFullPath).Distinct().ToList();

        if (string.IsNullOrWhiteSpace(path))
            return Results.Ok(new FolderListingDto("", null, roots.Select(r => new FolderEntryDto(r, true, null, null)).ToList()));

        var full = Path.GetFullPath(path);
        if (!roots.Any(r => full.Equals(r, StringComparison.Ordinal) || full.StartsWith(r.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            return Problems.Create(StatusCodes.Status403Forbidden, "forbidden", "That folder is outside the folders the server lets you browse.");
        if (!Directory.Exists(full))
            return Problems.NotFound("No such folder.");

        var entries = new DirectoryInfo(full).EnumerateDirectories()
            .Where(d => !d.Name.StartsWith('.'))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new FolderEntryDto(d.Name, true, null, d.LastWriteTimeUtc))
            .ToList();
        var parent = roots.Contains(full) ? null : Path.GetDirectoryName(full);
        return Results.Ok(new FolderListingDto(full, parent, entries));
    }

    private static Dictionary<string, string[]> Validate(SettingsDto s)
    {
        var errors = new Dictionary<string, string[]>();
        if (!ScheduleClock.IsValidZone(s.TimeZone))
            errors["/timeZone"] = [$"'{s.TimeZone}' isn't a time zone this server knows."];
        if (s.MaxConcurrentJobs < 1) errors["/maxConcurrentJobs"] = ["Must be at least 1."];
        if (s.MaxConcurrentPerDevice < 1) errors["/maxConcurrentPerDevice"] = ["Must be at least 1."];
        if (s.LogRetentionDays < 1) errors["/logRetentionDays"] = ["Must be at least 1."];
        if (s.RetryMaxAttempts < 1) errors["/retryMaxAttempts"] = ["Must be at least 1."];
        if (s.MaxRunHistoryEntries < 1) errors["/maxRunHistoryEntries"] = ["Must be at least 1."];
        if (s.PushSafetyMinimumPercent is < 0 or > 100) errors["/pushSafetyMinimumPercent"] = ["Must be between 0 and 100."];
        if (s.BandwidthThrottleKBps is < 1) errors["/bandwidthThrottleKBps"] = ["Must be at least 1, or null for no limit."];
        if (s.ConflictPolicy is not ("ask" or "newestWins")) errors["/conflictPolicy"] = ["Must be newestWins or ask."];
        foreach (var (name, value) in new[] { ("start", s.Notifications.QuietHours.Start), ("end", s.Notifications.QuietHours.End) })
        {
            if (!TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                errors[$"/notifications/quietHours/{name}"] = ["Must be HH:mm."];
        }
        foreach (var (name, value) in new[] { ("conflictRetentionDays", s.ConflictRetentionDays), ("checkpointRetentionDays", s.CheckpointRetentionDays),
                     ("rollbackPointsToKeep", s.RollbackPointsToKeep), ("retryBackoffSeconds", s.RetryBackoffSeconds), ("fileDetailRunsKept", s.FileDetailRunsKept), ("fullPullIntervalDays", s.FullPullIntervalDays) })
        {
            if (value < 0)
                errors[$"/{name}"] = ["Must not be negative."];
        }
        return errors;
    }

    private static void Apply(SettingsDto s, GlobalSettings settings)
    {
        settings.TimeZone = string.IsNullOrWhiteSpace(s.TimeZone) ? null : s.TimeZone.Trim();
        settings.LogRetentionDays = s.LogRetentionDays;
        settings.ConflictRetentionDays = s.ConflictRetentionDays;
        settings.PerLogFileMaxBytes = s.PerLogFileMaxBytes;
        settings.MaxConcurrentJobs = s.MaxConcurrentJobs;
        settings.MaxConcurrentPerDevice = s.MaxConcurrentPerDevice;
        settings.ShowInfoNotifications = s.Notifications.ShowInfo;
        settings.ShowErrorNotifications = s.Notifications.ShowErrors;
        settings.QuietHoursEnabled = s.Notifications.QuietHours.Enabled;
        settings.QuietHoursStart = TimeOnly.ParseExact(s.Notifications.QuietHours.Start, "HH:mm", CultureInfo.InvariantCulture);
        settings.QuietHoursEnd = TimeOnly.ParseExact(s.Notifications.QuietHours.End, "HH:mm", CultureInfo.InvariantCulture);
        settings.CheckpointRetentionDays = s.CheckpointRetentionDays;
        settings.RollbackPointsToKeep = s.RollbackPointsToKeep;
        settings.BandwidthThrottleKBps = s.BandwidthThrottleKBps;
        settings.RetryMaxAttempts = s.RetryMaxAttempts;
        settings.RetryBackoffSeconds = s.RetryBackoffSeconds;
        settings.PushSafetyMinimumPercent = s.PushSafetyMinimumPercent;
        settings.MaxRunHistoryEntries = s.MaxRunHistoryEntries;
        settings.FileDetailRunsKept = s.FileDetailRunsKept;
        settings.ConflictPolicy = s.ConflictPolicy == "ask" ? ConflictPolicy.Ask : ConflictPolicy.NewestWins;
        settings.BackupConflictLosers = s.BackupConflictLosers;
        settings.RunMissedSchedules = s.RunMissedSchedules;
        settings.CompanionBroadcastsEnabled = s.CompanionBroadcastsEnabled;
        settings.IncrementalPull = s.IncrementalPull;
        settings.FullPullIntervalDays = s.FullPullIntervalDays;
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch.ToList())
        {
            if (value is JsonObject child && target[key] is JsonObject existing)
                Merge(existing, child);
            else if (value is not null)
                target[key] = value.DeepClone();
        }
    }

    private static long? FolderSize(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The mount (Linux) or volume (Windows) a path is on, to tell whether a move crosses file systems.</summary>
    private static string MountOf(string path)
    {
        var full = Path.GetFullPath(path);
        return DriveInfo.GetDrives()
            .Select(d => d.RootDirectory.FullName)
            .Where(root => full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault() ?? "";
    }
}
