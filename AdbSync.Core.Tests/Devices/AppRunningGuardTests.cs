using AdbSync.Core.Services.Devices;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using NSubstitute;
using System.Text;

namespace AdbSync.Core.Tests.Devices;

public class AppRunningGuardTests
{
    private readonly IAdbClient _adbClient = Substitute.For<IAdbClient>();
    private readonly AppRunningGuard _guard;

    public AppRunningGuardTests() => _guard = new AppRunningGuard(_adbClient, TimeSpan.FromMilliseconds(1));

    private void SetPidofOutput(string serial, params string[] lines)
    {
        _adbClient
            .When(c => c.ExecuteRemoteCommandAsync(
                Arg.Any<string>(),
                Arg.Is<DeviceData>(d => d.Serial == serial),
                Arg.Any<IShellOutputReceiver>(),
                Arg.Any<Encoding>(),
                Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var receiver = call.ArgAt<IShellOutputReceiver>(2);
                foreach (var line in lines)
                    receiver.AddOutput(line);
                receiver.Flush();
            });
    }

    [Fact]
    public async Task FindRunningSerialAsync_NoDeviceHasOutput_ReturnsNull()
    {
        SetPidofOutput("192.168.0.40:41000");

        var running = await _guard.FindRunningSerialAsync("com.example.app", ["192.168.0.40:41000"]);

        Assert.Null(running);
    }

    [Fact]
    public async Task FindRunningSerialAsync_SecondDeviceHasPid_ReturnsItsSerial()
    {
        SetPidofOutput("device-a");
        SetPidofOutput("device-b", "12345");

        var running = await _guard.FindRunningSerialAsync("com.example.app", ["device-a", "device-b"]);

        Assert.Equal("device-b", running);
    }

    [Fact]
    public async Task FindRunningSerialAsync_StopsAfterFirstMatch_DoesNotQueryRemainingDevices()
    {
        SetPidofOutput("device-a", "12345");
        SetPidofOutput("device-b", "should-not-be-checked");

        var running = await _guard.FindRunningSerialAsync("com.example.app", ["device-a", "device-b"]);

        Assert.Equal("device-a", running);
        await _adbClient.DidNotReceive().ExecuteRemoteCommandAsync(
            Arg.Any<string>(),
            Arg.Is<DeviceData>(d => d.Serial == "device-b"),
            Arg.Any<IShellOutputReceiver>(),
            Arg.Any<Encoding>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WaitUntilStoppedAsync_PollsUntilAppExits()
    {
        var outputs = new Queue<string?>(["111", "111", null]);
        _adbClient
            .When(c => c.ExecuteRemoteCommandAsync(
                Arg.Any<string>(), Arg.Any<DeviceData>(), Arg.Any<IShellOutputReceiver>(), Arg.Any<Encoding>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var receiver = call.ArgAt<IShellOutputReceiver>(2);
                if (outputs.Dequeue() is { } pid)
                    receiver.AddOutput(pid);
                receiver.Flush();
            });

        await _guard.WaitUntilStoppedAsync("com.example.app", "device-a");

        await _adbClient.Received(3).ExecuteRemoteCommandAsync(
            Arg.Is<string>(c => c.StartsWith("pidof com.example.app ") && c.Contains("dumpsys activity activities")),
            Arg.Is<DeviceData>(d => d.Serial == "device-a"),
            Arg.Any<IShellOutputReceiver>(),
            Arg.Any<Encoding>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WaitUntilStoppedAsync_ConnectionDrops_Throws()
    {
        _adbClient
            .ExecuteRemoteCommandAsync(
                Arg.Any<string>(), Arg.Any<DeviceData>(), Arg.Any<IShellOutputReceiver>(), Arg.Any<Encoding>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("connection closed")));

        await Assert.ThrowsAsync<IOException>(() => _guard.WaitUntilStoppedAsync("com.example.app", "device-a"));
    }
}
