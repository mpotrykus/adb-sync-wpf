using AdbSync.Core.Models.Config;
using AdbSync.Engine.Services;

namespace AdbSync.Server.Api;

public sealed record DeviceConnectionDto(string State, string? Endpoint, string? EndpointSource, DateTimeOffset? EndpointAt, DateTimeOffset? CompanionLastSeenAt);

public sealed record DeviceDto(
    Guid Id, string Name, string Transport, string? Host, string? Serial, int? StaticPort, IReadOnlyList<Guid> UsedByJobIds, DeviceConnectionDto Connection);

public static class DeviceEndpoints
{
    public static DeviceDto ToDto(DeviceConfig device, AppConfig config) => new(
        device.Id,
        device.Name,
        device.Serial is not null && device.Ip is null ? "usb" : "wifi",
        device.Ip,
        device.Serial,
        null,
        config.Jobs.Where(j => j.Devices.Any(b => b.DeviceId == device.Id || string.Equals(b.DeviceName, device.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(j => j.Id).ToList(),
        new DeviceConnectionDto(
            "unknown",
            device.CachedHostPort,
            device.CachedHostPort is null ? null : device.Ip is null ? "usb" : "mdns",
            device.CachedAt,
            null));

    public static void MapDeviceEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/devices", async (AppConfigService configService) =>
        {
            var config = await configService.GetAsync();
            return config.Devices.Select(d => ToDto(d, config)).ToList();
        });

        api.MapGet("/devices/{deviceId:guid}", async (Guid deviceId, AppConfigService configService) =>
        {
            var config = await configService.GetAsync();
            return config.Devices.FirstOrDefault(d => d.Id == deviceId) is { } device
                ? Results.Ok(ToDto(device, config))
                : Problems.NotFound("No device with that id.");
        });
    }
}
