using AdbSync.Core.Models.Transfer;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Transfer;

namespace AdbSync.Server;

/// <summary>
/// Stands in for every operation that changes a device: it logs the operation and waits instead of doing it. Reads
/// (listing, pulling) go through to the real device.
/// </summary>
public sealed class DeviceWriteSimulator(TimeSpan delay, ILogger<DeviceWriteSimulator> logger)
{
    public async Task WaitAsync(string operation, CancellationToken ct)
    {
        logger.LogInformation("Simulated device write (not performed): {Operation}, waiting {Delay}s", operation, delay.TotalSeconds);
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);
    }
}

public sealed class SimulatedWriteRemoteFileSystem(IRemoteFileSystem inner, DeviceWriteSimulator simulator) : IRemoteFileSystem, IAsyncDisposable
{
    public Task<IReadOnlyList<RemoteFileInfo>> ListDirectoryAsync(string remotePath, CancellationToken ct = default) =>
        inner.ListDirectoryAsync(remotePath, ct);

    public Task PullFileAsync(string remotePath, string localPath, CancellationToken ct = default) =>
        inner.PullFileAsync(remotePath, localPath, ct);

    public Task PushFileAsync(string localPath, string remotePath, DateTimeOffset modifiedUtc, CancellationToken ct = default) =>
        simulator.WaitAsync($"push {remotePath}", ct);

    public Task DeleteFileAsync(string remotePath, CancellationToken ct = default) =>
        simulator.WaitAsync($"delete {remotePath}", ct);

    public Task DeleteDirectoryRecursiveAsync(string remotePath, CancellationToken ct = default) =>
        simulator.WaitAsync($"delete directory {remotePath}", ct);

    public Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default) =>
        simulator.WaitAsync($"mkdir {remotePath}", ct);

    public ValueTask DisposeAsync() => inner is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
}

public sealed class SimulatedWriteRemoteFileSystemFactory(IRemoteFileSystemFactory inner, DeviceWriteSimulator simulator) : IRemoteFileSystemFactory
{
    public IRemoteFileSystem Create(string serial, TransferPolicy? policy = null) =>
        new SimulatedWriteRemoteFileSystem(inner.Create(serial, policy), simulator);
}

public sealed class SimulatedDeviceAppControl(DeviceWriteSimulator simulator) : IDeviceAppControl
{
    public async Task ForceStopAsync(string appPackage, string serial, CancellationToken ct = default) =>
        await simulator.WaitAsync($"force-stop {appPackage} on {serial}", ct);

    public async Task<bool> LaunchAsync(string appPackage, string serial, CancellationToken ct = default)
    {
        await simulator.WaitAsync($"launch {appPackage} on {serial}", ct);
        return true;
    }
}
