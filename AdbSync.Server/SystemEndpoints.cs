using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Persistence;
using AdbSync.Core.Services.Scheduling;
using AdbSync.Engine.Services;
using Microsoft.AspNetCore.Authorization;
using System.Reflection;

namespace AdbSync.Server;

public static class SystemEndpoints
{
    public const string ApiVersion = "v1";

    public static void MapSystemEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/info", async (ServerOptions options, AppConfigService configService) =>
        {
            var config = await configService.GetAsync();
            return Results.Ok(new ServerInfo(
                options.Name,
                ServerVersion,
                [ApiVersion],
                new Dictionary<string, string>(),
                ScheduleClock.Zone(config.Settings).Id,
                []));
        }).AllowAnonymous();

        api.MapGet("/health", async (EngineDatabase database, AppConfigService configService, CancellationToken ct) =>
        {
            var checks = new Dictionary<string, HealthCheck>();
            try
            {
                await using var connection = await database.OpenAsync(ct);
                checks["data"] = new HealthCheck("ok", null);
            }
            catch (Exception ex)
            {
                checks["data"] = new HealthCheck("unavailable", ex.Message);
                return Results.Json(new Health("unavailable", checks), statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var config = await configService.GetAsync();
            var unavailable = config.Jobs
                .Select(job => ProjectFolders.Unavailable(job.Resolve(config.Settings), job.Name))
                .OfType<string>()
                .Distinct()
                .ToList();
            checks["projects"] = unavailable.Count == 0
                ? new HealthCheck("ok", null)
                : new HealthCheck("degraded", string.Join(" ", unavailable));

            return Results.Ok(new Health(unavailable.Count == 0 ? "ok" : "degraded", checks));
        }).AllowAnonymous();
    }

    private static string ServerVersion =>
        typeof(SystemEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}

public sealed record ServerInfo(
    string Name, string ServerVersion, IReadOnlyList<string> ApiVersions, IReadOnlyDictionary<string, string> MinClientVersions,
    string TimeZone, IReadOnlyList<string> Features);

public sealed record Health(string Status, IReadOnlyDictionary<string, HealthCheck> Checks);

public sealed record HealthCheck(string Status, string? Detail);
