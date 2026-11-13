namespace AdbSync.Server;

/// <summary>
/// Switches for side-effecting parts of the server, bound from the <c>Features</c> configuration section (appsettings
/// files or <c>Features__RunJobs=false</c> style environment variables). Everything is on unless turned off; the
/// Development appsettings turns the device-touching parts off so a dev server holding a copy of real data can't act on it.
/// </summary>
public sealed class FeatureFlags
{
    public const string SectionName = "Features";

    public bool Scheduler { get; init; } = true;
    public bool ChangeWatch { get; init; } = true;
    public bool CompanionBroadcast { get; init; } = true;

    /// <summary>Runs (manual, run-all, scheduled, on-change), snapshots, rollbacks, conflict resolution and app close-and-sync.</summary>
    public bool RunJobs { get; init; } = true;

    /// <summary>
    /// When off, anything that would change a device (push, delete, mkdir, app force-stop and launch) is logged and
    /// replaced by a wait of <see cref="SimulatedWriteDelaySeconds"/>. Reads still hit the real device.
    /// </summary>
    public bool DeviceWrites { get; init; } = true;

    public double SimulatedWriteDelaySeconds { get; init; } = 10;
}

public static class FeatureFlagEndpoints
{
    /// <summary>Rejects the endpoint with 403 <c>feature_disabled</c> while <see cref="FeatureFlags.RunJobs"/> is off.</summary>
    public static TBuilder RequireRunJobs<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<FeatureFlags>().RunJobs
                ? await next(context)
                : Problems.Create(StatusCodes.Status403Forbidden, "feature_disabled", "Running jobs is turned off on this server (Features:RunJobs)."));
}
