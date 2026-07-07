using AdbSync.Core.Transfer;

namespace AdbSync.Core.Tests.Transfer;

/// <summary>Records every invocation and dispatches to a handler keyed by the adb subcommand (args[1], e.g. "pull"/"push"/"shell").</summary>
public sealed class FakeAdbProcessRunner : IAdbProcessRunner
{
    public List<IReadOnlyList<string>> Calls { get; } = [];
    public Dictionary<string, Func<IReadOnlyList<string>, AdbProcessResult>> Handlers { get; } = [];
    public AdbProcessResult Default { get; set; } = new(0, "", "");

    public Task<AdbProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        Calls.Add(arguments);
        // Every call is shaped "-s <serial> <subcommand> ...".
        var subcommand = arguments.Count > 2 ? arguments[2] : string.Empty;
        var result = Handlers.TryGetValue(subcommand, out var handler) ? handler(arguments) : Default;
        return Task.FromResult(result);
    }
}
