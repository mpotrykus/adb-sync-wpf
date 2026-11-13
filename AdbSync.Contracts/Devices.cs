namespace AdbSync.Contracts;

public sealed record DeviceConnectionDto(string State, string? Endpoint, string? EndpointSource, DateTimeOffset? EndpointAt, DateTimeOffset? CompanionLastSeenAt);

public sealed record DeviceInputDto(string? Name, string? Transport, string? Host, string? Serial, int? StaticPort);

public sealed record DeviceDto(
    Guid Id, string Name, string Transport, string? Host, string? Serial, int? StaticPort, IReadOnlyList<Guid> UsedByJobIds, DeviceConnectionDto Connection);

public sealed record ConnectionTestResultDto(bool Connected, string? Serial, string? Endpoint, string? Code, string? Detail);

public sealed record AdbPairRequestDto(string? Host, int Port, string? Code);

public sealed record EndpointReportDto(int Port, string? Host);

public sealed record DiscoveredDeviceDto(string Source, string Label, string? Serial, string? Endpoint, Guid? MatchedDeviceId);

public sealed record FolderEntryDto(string Name, bool IsDirectory, long? Size, DateTimeOffset? ModifiedAt);

public sealed record FolderListingDto(string Path, string? Parent, IReadOnlyList<FolderEntryDto> Entries);
