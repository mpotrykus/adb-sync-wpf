namespace AdbSync.Contracts;

public sealed record PairRequest(string? Code, string? ClientName, string? ClientKind, string? ClientVersion);

public sealed record PairResponse(Guid ClientId, string Token);

public sealed record PairingCodeResponse(string Code, DateTimeOffset ExpiresAt, string QrPayload);

public sealed record ClientResponse(
    Guid Id, string Name, string Kind, string? Version, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool Current);
