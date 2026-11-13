using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Persistence;
using AdbSync.Server.Auth;

namespace AdbSync.Server;

/// <summary>
/// <c>adbsync-server pair</c> (in Docker: <c>docker exec adbsync adbsync-server pair</c>): prints a one-time code for
/// pairing the first client. Runs alongside the server and doesn't take its lock; it only adds a pairing code.
/// </summary>
public static class PairCommand
{
    public static async Task<int> RunAsync(AppPaths paths, ServerOptions options)
    {
        var identity = new ServerIdentity(paths, options);
        var code = await new AuthStore(new EngineDatabase(paths), TimeProvider.System).CreatePairingCodeAsync();
        Console.WriteLine($"""
            Pairing code: {code.Code}
            Expires:      {code.ExpiresAt.ToLocalTime():HH:mm} ({AuthStore.CodeLifetime.TotalMinutes:0} minutes, single use)
            Server:       {identity.PublicUrl}
            Fingerprint:  {identity.Fingerprint}
            Pairing link: {identity.PairingPayload(code.Code)}
            """);
        return 0;
    }
}
