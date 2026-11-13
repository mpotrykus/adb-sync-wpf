using System.Net;

namespace AdbSync.Server;

/// <summary>Server settings from the environment. Paths come from <c>ADBSYNC_DATA_DIR</c> / <c>ADBSYNC_PROJECTS_DIR</c> via AppPaths.</summary>
public sealed record ServerOptions(int Port, string Name, IPAddress ListenAddress)
{
    public const string PortVariable = "ADBSYNC_PORT";
    public const string NameVariable = "ADBSYNC_SERVER_NAME";
    public const string ListenVariable = "ADBSYNC_LISTEN";
    public const int DefaultPort = 47615;

    public static ServerOptions FromEnvironment() => new(
        int.TryParse(Environment.GetEnvironmentVariable(PortVariable), out var port) && port is > 0 and < 65536 ? port : DefaultPort,
        Environment.GetEnvironmentVariable(NameVariable) is { Length: > 0 } name ? name : Environment.MachineName,
        IPAddress.TryParse(Environment.GetEnvironmentVariable(ListenVariable), out var address) ? address : IPAddress.Any);
}
