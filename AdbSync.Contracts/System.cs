namespace AdbSync.Contracts;

public sealed record ServerInfo(
    string Name, string ServerVersion, IReadOnlyList<string> ApiVersions, IReadOnlyDictionary<string, string> MinClientVersions,
    string TimeZone, IReadOnlyList<string> Features);

public sealed record Health(string Status, IReadOnlyDictionary<string, HealthCheck> Checks);

public sealed record HealthCheck(string Status, string? Detail);
