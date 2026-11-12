using AdbSync.Core.Services.Transfer;
using Microsoft.Extensions.Logging.Abstractions;
using AdbSync.Core.Models.Transfer;

namespace AdbSync.Server.Tests;

public class SimulatedDeviceWritesTests
{
    private static DeviceWriteSimulator Simulator(TimeSpan delay) => new(delay, NullLogger<DeviceWriteSimulator>.Instance);

    private sealed class RecordingFileSystem : IRemoteFileSystem
    {
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<RemoteFileInfo>> ListDirectoryAsync(string remotePath, CancellationToken ct = default)
        {
            Calls.Add($"list {remotePath}");
            return Task.FromResult<IReadOnlyList<RemoteFileInfo>>([]);
        }

        public Task PullFileAsync(string remotePath, string localPath, CancellationToken ct = default) =>
            Record($"pull {remotePath}");

        public Task PushFileAsync(string localPath, string remotePath, DateTimeOffset modifiedUtc, CancellationToken ct = default) =>
            Record($"push {remotePath}");

        public Task DeleteFileAsync(string remotePath, CancellationToken ct = default) => Record($"delete {remotePath}");
        public Task DeleteDirectoryRecursiveAsync(string remotePath, CancellationToken ct = default) => Record($"rmdir {remotePath}");
        public Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default) => Record($"mkdir {remotePath}");

        private Task Record(string call)
        {
            Calls.Add(call);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Writes_DoNotReachTheDevice()
    {
        var inner = new RecordingFileSystem();
        var fs = new SimulatedWriteRemoteFileSystem(inner, Simulator(TimeSpan.Zero));

        await fs.PushFileAsync("a", "/sdcard/a", DateTimeOffset.UtcNow);
        await fs.DeleteFileAsync("/sdcard/a");
        await fs.DeleteDirectoryRecursiveAsync("/sdcard/d");
        await fs.CreateDirectoryAsync("/sdcard/d");

        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task Reads_GoToTheDevice()
    {
        var inner = new RecordingFileSystem();
        var fs = new SimulatedWriteRemoteFileSystem(inner, Simulator(TimeSpan.Zero));

        await fs.ListDirectoryAsync("/sdcard");
        await fs.PullFileAsync("/sdcard/a", "a");

        Assert.Equal(["list /sdcard", "pull /sdcard/a"], inner.Calls);
    }

    [Fact]
    public async Task Writes_WaitForTheConfiguredDelay_AndCanBeCancelled()
    {
        var fs = new SimulatedWriteRemoteFileSystem(new RecordingFileSystem(), Simulator(TimeSpan.FromMinutes(5)));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fs.CreateDirectoryAsync("/sdcard/d", cts.Token));
    }

    [Fact]
    public async Task AppControl_DoesNotTouchTheDevice()
    {
        var control = new SimulatedDeviceAppControl(Simulator(TimeSpan.Zero));

        await control.ForceStopAsync("com.example.app", "serial");
        Assert.True(await control.LaunchAsync("com.example.app", "serial"));
    }
}
