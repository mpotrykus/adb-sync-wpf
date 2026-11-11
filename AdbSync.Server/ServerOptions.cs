namespace AdbSync.Server;

/// <summary>Server settings from the environment. Paths come from <c>ADBSYNC_DATA_DIR</c> / <c>ADBSYNC_PROJECTS_DIR</c> via AppPaths.</summary>
public sealed record ServerOptions(int Port, string Name)
{
    public const string PortVariable = "ADBSYNC_PORT";
    public const string NameVariable = "ADBSYNC_SERVER_NAME";
    public const int DefaultPort = 47615;

    public static ServerOptions FromEnvironment() => new(
        int.TryParse(Environment.GetEnvironmentVariable(PortVariable), out var port) && port is > 0 and < 65536 ? port : DefaultPort,
        Environment.GetEnvironmentVariable(NameVariable) is { Length: > 0 } name ? name : Environment.MachineName);

    /// <summary>
    /// Loopback only until client auth exists (Phase 2 step 10): the API can read and write the user's files, so it
    /// must not be reachable from the network without it.
    /// </summary>
    public string ListenUrl => $"http://127.0.0.1:{Port}";
}
