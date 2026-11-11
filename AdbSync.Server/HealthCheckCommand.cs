namespace AdbSync.Server;

/// <summary><c>adbsync-server healthcheck</c>: the container's HEALTHCHECK, since the runtime image has no curl.</summary>
public static class HealthCheckCommand
{
    public static async Task<int> RunAsync(ServerOptions options)
    {
        // Only ever talks to this machine's own server, so the certificate (self-signed, or issued for another name)
        // isn't checked.
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync($"https://127.0.0.1:{options.Port}/api/{SystemEndpoints.ApiVersion}/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
