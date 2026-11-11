using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Devices;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Transfer;
using AdbSync.Engine.Services;
using AdbSync.Server.Events;
using AdvancedSharpAdbClient;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdbSync.Server.Api;

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

public static class DeviceEndpoints
{
    private static readonly TimeSpan DeviceCallTimeout = TimeSpan.FromSeconds(30);

    public static DeviceDto ToDto(DeviceConfig device, AppConfig config) => new(
        device.Id,
        device.Name,
        Transport(device),
        device.Ip,
        device.Serial,
        device.StaticPort,
        BoundJobs(device, config).Select(j => j.Id).ToList(),
        new DeviceConnectionDto(
            "unknown",
            device.CachedHostPort,
            device.CachedHostPort is null ? null : device.Ip is null ? "usb" : "mdns",
            device.CachedAt,
            null));

    private static string Transport(DeviceConfig device) => device.Ip is null && device.Serial is not null ? "usb" : "wifi";

    private static IEnumerable<SyncJobConfig> BoundJobs(DeviceConfig device, AppConfig config) =>
        config.Jobs.Where(j => j.Devices.Any(b => b.DeviceId == device.Id || string.Equals(b.DeviceName, device.Name, StringComparison.OrdinalIgnoreCase)));

    private static string ETag(DeviceDto dto) =>
        $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dto with { Connection = null!, UsedByJobIds = [] }, JobMapping.Json))))[..16].ToLowerInvariant()}\"";

    private static Dictionary<string, string[]> Validate(DeviceInputDto input, AppConfig config, Guid? existingId)
    {
        var errors = new Dictionary<string, string[]>();
        if (ConfigNames.Problem(input.Name) is { } nameProblem)
            errors["/name"] = [nameProblem];
        else if (!ConfigNames.IsUnique(input.Name!, config.Devices.Where(d => d.Id != existingId).Select(d => d.Name)))
            errors["/name"] = [$"A device named '{input.Name}' already exists."];

        switch (input.Transport)
        {
            case "wifi" when string.IsNullOrWhiteSpace(input.Host):
                errors["/host"] = ["A wifi device needs its IP address or hostname."];
                break;
            case "usb" when string.IsNullOrWhiteSpace(input.Serial):
                errors["/serial"] = ["A usb device needs its adb serial."];
                break;
            case "wifi" or "usb":
                break;
            default:
                errors["/transport"] = ["Must be wifi or usb."];
                break;
        }

        if (input.StaticPort is < 1 or > 65535)
            errors["/staticPort"] = ["Must be between 1 and 65535."];
        return errors;
    }

    private static void Apply(DeviceInputDto input, DeviceConfig device)
    {
        device.Name = input.Name!.Trim();
        if (input.Transport == "usb")
        {
            device.Ip = null;
            device.Serial = input.Serial!.Trim();
            device.StaticPort = null;
        }
        else
        {
            if (!string.Equals(device.Ip, input.Host!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                device.CachedHostPort = null;
                device.CachedAt = null;
            }
            device.Ip = input.Host!.Trim();
            device.Serial = null;
            device.StaticPort = input.StaticPort;
        }
    }

    public static void MapDeviceEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/devices", async (AppConfigService configService) =>
        {
            var config = await configService.GetAsync();
            return config.Devices.Select(d => ToDto(d, config)).ToList();
        });

        api.MapGet("/devices/{deviceId:guid}", async (Guid deviceId, HttpContext http, AppConfigService configService) =>
        {
            var config = await configService.GetAsync();
            if (config.Devices.FirstOrDefault(d => d.Id == deviceId) is not { } device)
                return DeviceNotFound();
            var dto = ToDto(device, config);
            http.Response.Headers.ETag = ETag(dto);
            return Results.Ok(dto);
        });

        api.MapPost("/devices", async (DeviceInputDto input, HttpContext http, ConfigEditor editor, AppConfigService configService, EventBus bus) =>
            await editor.EditAsync(async config =>
            {
                var errors = Validate(input, config, null);
                if (errors.Count > 0)
                    return Invalid(errors);
                var device = new DeviceConfig();
                Apply(input, device);
                config.Devices.Add(device);
                await configService.SaveAsync();
                var dto = ToDto(device, config);
                bus.Publish("device.changed", dto);
                http.Response.Headers.ETag = ETag(dto);
                return Results.Created($"/api/v1/devices/{device.Id}", dto);
            }));

        api.MapPatch("/devices/{deviceId:guid}", async (
            Guid deviceId, JsonObject patch, HttpContext http, ConfigEditor editor, AppConfigService configService, EventBus bus) =>
            await editor.EditAsync(async config =>
            {
                if (config.Devices.FirstOrDefault(d => d.Id == deviceId) is not { } device)
                    return DeviceNotFound();
                var current = ToDto(device, config);
                if (http.Request.Headers.IfMatch.ToString() is { Length: > 0 } ifMatch && ifMatch != ETag(current))
                    return Problems.Create(StatusCodes.Status412PreconditionFailed, "precondition_failed",
                        "The device changed since you loaded it. Reload it and try again.");

                var merged = JsonSerializer.SerializeToNode(new DeviceInputDto(current.Name, current.Transport, current.Host, current.Serial, current.StaticPort), JobMapping.Json)!;
                foreach (var (key, value) in patch)
                    merged[key] = value?.DeepClone();
                var input = merged.Deserialize<DeviceInputDto>(JobMapping.Json)!;
                var errors = Validate(input, config, deviceId);
                if (errors.Count > 0)
                    return Invalid(errors);
                // Sync state (manifests, conflicts, push-safety counts, backup folders) is stored under the device's
                // name, so a rename is only safe while nothing has synced with it under the old one.
                if (!string.Equals(device.Name, input.Name!.Trim(), StringComparison.Ordinal) && BoundJobs(device, config).Any())
                    return Problems.Create(StatusCodes.Status409Conflict, "device_in_use",
                        "Remove the device from its jobs before renaming it.");

                Apply(input, device);
                await configService.SaveAsync();
                var dto = ToDto(device, config);
                bus.Publish("device.changed", dto);
                http.Response.Headers.ETag = ETag(dto);
                return Results.Ok(dto);
            })).Accepts<JsonObject>("application/merge-patch+json", "application/json");

        api.MapDelete("/devices/{deviceId:guid}", async (Guid deviceId, ConfigEditor editor, AppConfigService configService, EventBus bus) =>
            await editor.EditAsync(async config =>
            {
                if (config.Devices.FirstOrDefault(d => d.Id == deviceId) is not { } device)
                    return DeviceNotFound();
                if (BoundJobs(device, config).Any())
                    return Problems.Create(StatusCodes.Status409Conflict, "device_in_use", "Remove the device from its jobs first.");
                config.Devices.Remove(device);
                await configService.SaveAsync();
                bus.Publish("device.deleted", new { id = deviceId });
                return Results.NoContent();
            }));

        api.MapPost("/devices/{deviceId:guid}/test", async (Guid deviceId, AppConfigService configService, IAdbDeviceResolver resolver, EventBus bus, CancellationToken ct) =>
        {
            var config = await configService.GetAsync();
            if (config.Devices.FirstOrDefault(d => d.Id == deviceId) is not { } device)
                return DeviceNotFound();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DeviceCallTimeout);
            try
            {
                var serial = await resolver.EnsureConnectedAsync(device, timeout.Token);
                await configService.SaveAsync();
                bus.Publish("device.connection", new { deviceId, connection = ToDto(device, config).Connection with { State = "connected" } });
                return Results.Ok(new ConnectionTestResultDto(true, serial, device.CachedHostPort ?? serial, null, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return Results.Ok(new ConnectionTestResultDto(false, null, null, "device_unreachable", ex.Message));
            }
        });

        api.MapPost("/devices/{deviceId:guid}/pair", async (
            Guid deviceId, AdbPairRequestDto request, HttpContext http, AppConfigService configService, IAdbDeviceResolver resolver, CancellationToken ct) =>
        {
            if (request.Code is not { Length: 6 } code || !code.All(char.IsAsciiDigit) || request.Port is < 1 or > 65535)
                return Problems.Validation("Expected the pairing port and the 6-digit code shown on the phone.");
            var config = await configService.GetAsync();
            if (config.Devices.FirstOrDefault(d => d.Id == deviceId) is not { } device)
                return DeviceNotFound();
            var host = request.Host ?? device.Ip ?? http.Connection.RemoteIpAddress?.MapToIPv4().ToString();
            if (host is null)
                return Problems.Validation("This device has no host to pair with.");

            device.Ip = host;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DeviceCallTimeout);
            try
            {
                await resolver.PairAtAsync(device, request.Port, code, timeout.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return Problems.Create(StatusCodes.Status502BadGateway, "pairing_failed", "Pairing with the phone failed.", ex.Message);
            }
            await configService.SaveAsync();
            return Results.Ok(ToDto(device, config));
        });

        api.MapPost("/devices/{deviceId:guid}/endpoint", async (
            Guid deviceId, EndpointReportDto report, HttpContext http, ConfigEditor editor, AppConfigService configService, EventBus bus) =>
            await editor.EditAsync(async config =>
            {
                if (report.Port is < 1 or > 65535)
                    return Problems.Validation("port must be between 1 and 65535.");
                if (config.Devices.FirstOrDefault(d => d.Id == deviceId) is not { } device)
                    return DeviceNotFound();
                if (device.Ip is null)
                    return Problems.Validation("Not a wireless device.");
                var host = report.Host ?? http.Connection.RemoteIpAddress?.MapToIPv4().ToString();
                if (host is null || !IPAddress.TryParse(host, out _))
                    return Problems.Validation("host must be an IP address.");

                device.Ip = host;
                device.CachedHostPort = $"{host}:{report.Port}";
                device.CachedAt = DateTimeOffset.UtcNow;
                await configService.SaveAsync();
                bus.Publish("device.connection", new { deviceId, connection = ToDto(device, config).Connection with { EndpointSource = "companion" } });
                return Results.NoContent();
            }));

        api.MapGet("/devices/{deviceId:guid}/browse", async (
            Guid deviceId, string path, AppConfigService configService, IAdbDeviceResolver resolver, IRemoteFileSystemFactory files, CancellationToken ct) =>
        {
            if (!path.StartsWith('/'))
                return Problems.Validation("path must be an absolute device path.");
            var config = await configService.GetAsync();
            if (config.Devices.FirstOrDefault(d => d.Id == deviceId) is not { } device)
                return DeviceNotFound();
            return await OnDeviceAsync(device, resolver, ct, async serial =>
            {
                var remote = files.Create(serial);
                await using var connection = remote as IAsyncDisposable;
                var entries = await remote.ListDirectoryAsync(path, ct);
                var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
                return Results.Ok(new FolderListingDto(
                    trimmed,
                    trimmed == "/" ? null : (trimmed[..trimmed.LastIndexOf('/')] is { Length: > 0 } parent ? parent : "/"),
                    entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(e => new FolderEntryDto(e.Name, e.IsDirectory, e.IsDirectory ? null : e.Size, e.ModifiedUtc)).ToList()));
            });
        });

        api.MapGet("/devices/{deviceId:guid}/packages", async (
            Guid deviceId, AppConfigService configService, IAdbDeviceResolver resolver, IDevicePackageLister packages, CancellationToken ct) =>
        {
            var config = await configService.GetAsync();
            if (config.Devices.FirstOrDefault(d => d.Id == deviceId) is not { } device)
                return DeviceNotFound();
            return await OnDeviceAsync(device, resolver, ct, async serial => Results.Ok(await packages.ListInstalledPackagesAsync(serial, ct)));
        });

        api.MapGet("/devices/discovered", async (AppConfigService configService, IAdbClient adb, IMdnsBrowser mdns, CancellationToken ct) =>
        {
            var config = await configService.GetAsync();
            var found = new List<DiscoveredDeviceDto>();
            try
            {
                foreach (var d in await adb.GetDevicesAsync(ct))
                {
                    var ip = d.Serial.Split(':')[0];
                    var match = config.Devices.FirstOrDefault(x => x.Serial == d.Serial || x.CachedHostPort == d.Serial || x.Ip == ip);
                    found.Add(new DiscoveredDeviceDto("adb", d.Model is { Length: > 0 } model ? model : d.Serial, d.Serial,
                        d.Serial.Contains(':') ? d.Serial : null, match?.Id));
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }
            foreach (var (type, source) in new[] { ("_adb-tls-connect._tcp", "mdnsConnect"), ("_adb-tls-pairing._tcp", "mdnsPairing") })
            {
                foreach (var a in await mdns.BrowseAsync(type, TimeSpan.FromSeconds(2), ct))
                {
                    var ip = a.Addresses.FirstOrDefault()?.ToString();
                    found.Add(new DiscoveredDeviceDto(source, a.InstanceName, null, ip is null ? null : $"{ip}:{a.Port}",
                        config.Devices.FirstOrDefault(x => x.Ip is not null && a.Addresses.Any(addr => addr.ToString() == x.Ip))?.Id));
                }
            }
            return found;
        });
    }

    private static async Task<IResult> OnDeviceAsync(DeviceConfig device, IAdbDeviceResolver resolver, CancellationToken ct, Func<string, Task<IResult>> action)
    {
        string serial;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DeviceCallTimeout);
        try
        {
            serial = await resolver.EnsureConnectedAsync(device, timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Problems.Create(StatusCodes.Status502BadGateway, "device_unreachable", "Couldn't connect to the device.", ex.Message);
        }
        try
        {
            return await action(serial);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Problems.Create(StatusCodes.Status502BadGateway, "device_command_failed", "The device command failed.", ex.Message);
        }
    }

    private static IResult DeviceNotFound() => Problems.NotFound("No device with that id.");

    private static IResult Invalid(Dictionary<string, string[]> errors) =>
        Results.Problem(
            title: "The device isn't valid.",
            statusCode: StatusCodes.Status400BadRequest,
            type: "https://adbsync.dev/problems/validation_failed",
            extensions: new Dictionary<string, object?> { ["code"] = "validation_failed", ["errors"] = errors });
}
