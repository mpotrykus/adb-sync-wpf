using AdbSync.Core.Models.Devices;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Transfer;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace AdbSync.Core.Tests.Orchestration.Fakes;

/// <summary>Lets a test drive a live "inotifyd" stream (via <see cref="EmitSignal"/>) or a polling snapshot sequence without touching a real device.</summary>
public sealed class FakeDeviceChangeWatcher : IDeviceChangeWatcher
{
    private readonly ConcurrentBag<Channel<ChangeSignal>> _channels = [];

    public WatchAvailability Availability { get; set; } = new(true, "Live watch supported");
    public Func<int, bool>? ThrowOnWatchCall { get; set; }
    public Func<int, string>? SnapshotSequence { get; set; }

    public int WatchAsyncCallCount { get; private set; }
    public int CheckAvailabilityCallCount { get; private set; }
    private int _snapshotCallCount;

    public Task<WatchAvailability> CheckAvailabilityAsync(string serial, string remotePath, IExcludeMatcher exclude, CancellationToken ct = default)
    {
        CheckAvailabilityCallCount++;
        return Task.FromResult(Availability);
    }

    public Task<IReadOnlyList<string>> EnumerateSubdirectoriesAsync(string serial, string remotePath, IExcludeMatcher exclude, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([remotePath]);

    public Task<string> SnapshotAsync(string serial, string remotePath, CancellationToken ct = default) =>
        Task.FromResult(SnapshotSequence?.Invoke(_snapshotCallCount++) ?? "unchanged");

    public async IAsyncEnumerable<ChangeSignal> WatchAsync(
        string serial, IReadOnlyList<string> paths, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var callIndex = WatchAsyncCallCount++;
        if (ThrowOnWatchCall?.Invoke(callIndex) == true)
            throw new InvalidOperationException($"simulated stream failure on attempt {callIndex}");

        var channel = Channel.CreateUnbounded<ChangeSignal>();
        _channels.Add(channel);
        while (await channel.Reader.WaitToReadAsync(ct))
        {
            while (channel.Reader.TryRead(out var signal))
                yield return signal;
        }
    }

    /// <summary>Pushes one event to every currently-open watch stream, as if inotifyd printed a line on the device.</summary>
    public void EmitSignal()
    {
        foreach (var channel in _channels)
            channel.Writer.TryWrite(new ChangeSignal());
    }
}
