using AdbSync.Server.Events;
using Microsoft.AspNetCore.Authorization;

namespace AdbSync.Server.Auth;

public static class AuthEndpoints
{
    public const string PairRateLimitPolicy = "pair";
    private const string ClientIdItem = "adbsync.clientId";
    private static readonly HashSet<string> ClientKinds = ["android", "windows", "web", "cli", "other"];

    /// <summary>Every endpoint in the group needs a client token unless it's marked AllowAnonymous.</summary>
    public static RouteGroupBuilder RequireClientToken(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (http.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
                return await next(context);

            var header = http.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Problems.Unauthorized();

            var store = http.RequestServices.GetRequiredService<AuthStore>();
            if (await store.AuthenticateAsync(header["Bearer ".Length..].Trim(), http.RequestAborted) is not { } clientId)
                return Problems.Unauthorized();

            http.Items[ClientIdItem] = clientId;
            return await next(context);
        });
        return group;
    }

    public static Guid? CurrentClientId(this HttpContext http) => http.Items[ClientIdItem] as Guid?;

    public static void MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/auth/pair", async (PairRequest request, AuthStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.ClientName))
                return Problems.Validation("code and clientName are required.");
            if (!ClientKinds.Contains(request.ClientKind ?? ""))
                return Problems.Validation($"clientKind must be one of: {string.Join(", ", ClientKinds)}.");

            var paired = await store.PairAsync(request.Code, request.ClientName.Trim(), request.ClientKind!, request.ClientVersion, ct);
            return paired is { } p
                ? Results.Created($"/api/v1/auth/clients/{p.Client.Id}", new PairResponse(p.Client.Id, p.Token))
                : Problems.Create(StatusCodes.Status401Unauthorized, "pairing_code_invalid", "That pairing code is wrong or has expired.");
        }).AllowAnonymous().RequireRateLimiting(PairRateLimitPolicy);

        api.MapPost("/auth/pairing-codes", async (AuthStore store, ServerIdentity identity, CancellationToken ct) =>
        {
            var code = await store.CreatePairingCodeAsync(ct);
            return Results.Created((string?)null, new PairingCodeResponse(code.Code, code.ExpiresAt, identity.PairingPayload(code.Code)));
        });

        api.MapGet("/auth/clients", async (HttpContext http, AuthStore store, CancellationToken ct) =>
        {
            var current = http.CurrentClientId();
            return (await store.ListAsync(ct))
                .Select(c => new ClientResponse(c.Id, c.Name, c.Kind, c.Version, c.CreatedAt, c.LastSeenAt, c.Id == current));
        });

        api.MapDelete("/auth/clients/{clientId:guid}", async (Guid clientId, AuthStore store, EventBus bus, CancellationToken ct) =>
        {
            if (!await store.RevokeAsync(clientId, ct))
                return Problems.NotFound("No client with that id.");
            bus.Publish("client.revoked", new { clientId }, onlyClient: clientId);
            bus.Disconnect(clientId);
            return Results.NoContent();
        });
    }
}

public sealed record PairRequest(string? Code, string? ClientName, string? ClientKind, string? ClientVersion);

public sealed record PairResponse(Guid ClientId, string Token);

public sealed record PairingCodeResponse(string Code, DateTimeOffset ExpiresAt, string QrPayload);

public sealed record ClientResponse(
    Guid Id, string Name, string Kind, string? Version, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool Current);
