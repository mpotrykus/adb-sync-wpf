using System.Collections.Concurrent;

namespace AdbSync.Core.Services.Orchestration;

public sealed class DeviceAccessGate : IDeviceAccessGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IAsyncDisposable> AcquireAsync(string deviceName, int maxConcurrent, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(deviceName, _ => new SemaphoreSlim(maxConcurrent, maxConcurrent));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    public bool IsBusy(string deviceName) =>
        _gates.TryGetValue(deviceName, out var gate) && gate.CurrentCount == 0;

    private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
