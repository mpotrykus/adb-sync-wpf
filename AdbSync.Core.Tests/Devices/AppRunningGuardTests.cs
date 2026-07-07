using System.Text;
using AdbSync.Core.Devices;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using NSubstitute;

namespace AdbSync.Core.Tests.Devices;

public class AppRunningGuardTests
{
    private readonly IAdbClient _adbClient = Substitute.For<IAdbClient>();
    private readonly AppRunningGuard _guard;

    public AppRunningGuardTests() => _guard = new AppRunningGuard(_adbClient);

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
    public async Task IsRunningAnywhereAsync_NoDeviceHasOutput_ReturnsFalse()
    {
        SetPidofOutput("192.168.0.40:41000");

        var running = await _guard.IsRunningAnywhereAsync("com.example.app", ["192.168.0.40:41000"]);

        Assert.False(running);
    }

    [Fact]
    public async Task IsRunningAnywhereAsync_SecondDeviceHasPid_ReturnsTrue()
    {
        SetPidofOutput("device-a");
        SetPidofOutput("device-b", "12345");

        var running = await _guard.IsRunningAnywhereAsync("com.example.app", ["device-a", "device-b"]);

        Assert.True(running);
    }

    [Fact]
    public async Task IsRunningAnywhereAsync_StopsAfterFirstMatch_DoesNotQueryRemainingDevices()
    {
        SetPidofOutput("device-a", "12345");
        SetPidofOutput("device-b", "should-not-be-checked");

        var running = await _guard.IsRunningAnywhereAsync("com.example.app", ["device-a", "device-b"]);

        Assert.True(running);
        await _adbClient.DidNotReceive().ExecuteRemoteCommandAsync(
            Arg.Any<string>(),
            Arg.Is<DeviceData>(d => d.Serial == "device-b"),
            Arg.Any<IShellOutputReceiver>(),
            Arg.Any<Encoding>(),
            Arg.Any<CancellationToken>());
    }
}
