using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Devices;
using AdbSync.Core.Services.Devices;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using NSubstitute;
using System.Net;

namespace AdbSync.Core.Tests.Devices;

public class AdbDeviceResolverTests
{
    private readonly IAdbClient _adbClient = Substitute.For<IAdbClient>();
    private readonly IMdnsBrowser _mdns = Substitute.For<IMdnsBrowser>();
    private readonly IAdbServer _adbServer = Substitute.For<IAdbServer>();
    private readonly AdbDeviceResolver _resolver;

    public AdbDeviceResolverTests()
    {
        _resolver = new AdbDeviceResolver(_adbClient, _mdns, _adbServer);
    }

    private static Task<IEnumerable<DeviceData>> Devices(params DeviceData[] devices) =>
        Task.FromResult<IEnumerable<DeviceData>>(devices);

    [Fact]
    public async Task EnsureConnectedAsync_UsbSerialDevice_ReturnsSerialWithoutAnyDiscovery()
    {
        var device = new DeviceConfig { Name = "BlueStacks", Serial = "emulator-5554" };

        var result = await _resolver.EnsureConnectedAsync(device);

        Assert.Equal("emulator-5554", result);
        await _adbClient.DidNotReceiveWithAnyArgs().GetDevicesAsync(default);
        await _mdns.DidNotReceiveWithAnyArgs().BrowseAsync(default!, default, default);
    }

    [Fact]
    public async Task EnsureConnectedAsync_AnyDevice_StartsAdbServerFirst()
    {
        var device = new DeviceConfig { Name = "BlueStacks", Serial = "emulator-5554" };

        await _resolver.EnsureConnectedAsync(device);

        await _adbServer.Received(1).StartServerAsync("adb", restartServerIfNewer: false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureConnectedAsync_CachedHostPortStillOnline_ReturnsCachedWithoutMdns()
    {
        var device = new DeviceConfig { Name = "S23+", Ip = "192.168.0.40", CachedHostPort = "192.168.0.40:41000" };
        _adbClient.GetDevicesAsync(Arg.Any<CancellationToken>())
            .Returns(Devices(new DeviceData { Serial = "192.168.0.40:41000", State = DeviceState.Online }));

        var result = await _resolver.EnsureConnectedAsync(device);

        Assert.Equal("192.168.0.40:41000", result);
        await _mdns.DidNotReceiveWithAnyArgs().BrowseAsync(default!, default, default);
    }

    [Fact]
    public async Task EnsureConnectedAsync_AlreadyConnectedViaAdbDevices_CachesAndReturnsWithoutMdns()
    {
        var device = new DeviceConfig { Name = "S23+", Ip = "192.168.0.40" };
        _adbClient.GetDevicesAsync(Arg.Any<CancellationToken>())
            .Returns(Devices(new DeviceData { Serial = "192.168.0.40:41000", State = DeviceState.Online }));

        var result = await _resolver.EnsureConnectedAsync(device);

        Assert.Equal("192.168.0.40:41000", result);
        Assert.Equal("192.168.0.40:41000", device.CachedHostPort);
        Assert.NotNull(device.CachedAt);
        await _mdns.DidNotReceiveWithAnyArgs().BrowseAsync(default!, default, default);
    }

    [Fact]
    public async Task EnsureConnectedAsync_NotConnected_DiscoversViaMdnsAndConnects()
    {
        var device = new DeviceConfig { Name = "S23+", Ip = "192.168.0.40" };
        _adbClient.GetDevicesAsync(Arg.Any<CancellationToken>())
            .Returns(
                Devices(), // no devices connected yet
                Devices(new DeviceData { Serial = "192.168.0.40:41000", State = DeviceState.Online })); // online after connect
        _mdns.BrowseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new List<MdnsAnnouncement>
            {
                new("instance", "host.local", 41000, [IPAddress.Parse("192.168.0.40")]),
            });

        var result = await _resolver.EnsureConnectedAsync(device);

        Assert.Equal("192.168.0.40:41000", result);
        Assert.Equal("192.168.0.40:41000", device.CachedHostPort);
        await _adbClient.Received(1).ConnectAsync("192.168.0.40", 41000, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureConnectedAsync_MdnsFindsNoMatchingAddress_ThrowsDeviceConnectException()
    {
        var device = new DeviceConfig { Name = "S23+", Ip = "192.168.0.40" };
        _adbClient.GetDevicesAsync(Arg.Any<CancellationToken>()).Returns(Devices());
        _mdns.BrowseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new List<MdnsAnnouncement>
            {
                new("instance", "other.local", 41000, [IPAddress.Parse("192.168.0.99")]),
            });

        var ex = await Assert.ThrowsAsync<DeviceConnectException>(() => _resolver.EnsureConnectedAsync(device));

        Assert.Equal("S23+", ex.DeviceName);
        await _adbClient.DidNotReceiveWithAnyArgs().ConnectAsync(default!, default, default);
    }

    [Fact]
    public async Task EnsureConnectedAsync_ConnectDoesNotComeOnline_ThrowsDeviceConnectException()
    {
        var device = new DeviceConfig { Name = "S23+", Ip = "192.168.0.40" };
        _adbClient.GetDevicesAsync(Arg.Any<CancellationToken>()).Returns(Devices()); // never comes online
        _mdns.BrowseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new List<MdnsAnnouncement>
            {
                new("instance", "host.local", 41000, [IPAddress.Parse("192.168.0.40")]),
            });

        await Assert.ThrowsAsync<DeviceConnectException>(() => _resolver.EnsureConnectedAsync(device));
    }

    [Fact]
    public async Task EnsureConnectedAsync_SameDeviceCalledConcurrently_SecondWaitsForFirstToRelease()
    {
        var device = new DeviceConfig { Name = "S23+", Serial = "emulator-5554" };
        var startServerGate = new TaskCompletionSource<StartServerResult>();
        var startServerCallCount = 0;
        _adbServer.StartServerAsync("adb", restartServerIfNewer: false, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref startServerCallCount);
                return startServerGate.Task;
            });

        var firstCall = _resolver.EnsureConnectedAsync(device);
        await Task.Delay(50); // let the first call actually enter and block on startServerGate

        var secondCall = _resolver.EnsureConnectedAsync(device);
        var wonRace = await Task.WhenAny(secondCall, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(secondCall, wonRace);
        // If the two calls weren't serialized, the second would have reached StartServerAsync by now too.
        Assert.Equal(1, startServerCallCount);

        startServerGate.SetResult(StartServerResult.Started);

        Assert.Equal("emulator-5554", await firstCall);
        Assert.Equal("emulator-5554", await secondCall);
        Assert.Equal(2, startServerCallCount);
    }

    [Fact]
    public async Task EnsureConnectedAsync_DifferentDevicesCalledConcurrently_BothProceedWithoutWaiting()
    {
        var deviceA = new DeviceConfig { Name = "S23+", Serial = "emulator-5554" };
        var deviceB = new DeviceConfig { Name = "BlueStacks", Serial = "emulator-5556" };
        var startServerGate = new TaskCompletionSource<StartServerResult>();
        var startServerCallCount = 0;
        // Only the first call (deviceA's) blocks - deviceA and deviceB are locked independently, so if deviceB
        // waited behind deviceA it would never reach this second, non-blocking invocation either.
        _adbServer.StartServerAsync("adb", restartServerIfNewer: false, Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref startServerCallCount) == 1
                ? startServerGate.Task
                : Task.FromResult(StartServerResult.Started));

        var firstCall = _resolver.EnsureConnectedAsync(deviceA);
        await Task.Delay(50); // let the first call actually enter and block on startServerGate

        var secondCall = _resolver.EnsureConnectedAsync(deviceB);
        var wonRace = await Task.WhenAny(secondCall, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(secondCall, wonRace);
        Assert.Equal("emulator-5556", await secondCall);

        startServerGate.SetResult(StartServerResult.Started);
        Assert.Equal("emulator-5554", await firstCall);
    }

    [Fact]
    public async Task PairAsync_AnnouncementFoundAndAdbConfirms_ReturnsHostPort()
    {
        var device = new DeviceConfig { Name = "S23+", Ip = "192.168.0.40" };
        _mdns.BrowseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new List<MdnsAnnouncement>
            {
                new("instance", "host.local", 37021, [IPAddress.Parse("192.168.0.40")]),
            });
        _adbClient.PairAsync("192.168.0.40", 37021, "123456", Arg.Any<CancellationToken>())
            .Returns("Successfully paired to 192.168.0.40:37021 [guid=abc]");

        var result = await _resolver.PairAsync(device, "123456");

        Assert.Equal("192.168.0.40:37021", result);
        await _mdns.Received(1).BrowseAsync("_adb-tls-pairing._tcp", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PairAsync_NoIpConfigured_Throws()
    {
        var device = new DeviceConfig { Name = "BlueStacks", Serial = "emulator-5554" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _resolver.PairAsync(device, "123456"));
    }

    [Fact]
    public async Task PairAsync_NoMatchingAnnouncement_ThrowsDevicePairException()
    {
        var device = new DeviceConfig { Name = "S23+", Ip = "192.168.0.40" };
        _mdns.BrowseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new List<MdnsAnnouncement>());

        var ex = await Assert.ThrowsAsync<DevicePairException>(() => _resolver.PairAsync(device, "123456"));

        Assert.Equal("S23+", ex.DeviceName);
        await _adbClient.DidNotReceiveWithAnyArgs().PairAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task PairAsync_AdbRejectsCode_ThrowsDevicePairException()
    {
        var device = new DeviceConfig { Name = "S23+", Ip = "192.168.0.40" };
        _mdns.BrowseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new List<MdnsAnnouncement>
            {
                new("instance", "host.local", 37021, [IPAddress.Parse("192.168.0.40")]),
            });
        _adbClient.PairAsync("192.168.0.40", 37021, "000000", Arg.Any<CancellationToken>())
            .Returns("Failed: Wrong pairing code");

        var ex = await Assert.ThrowsAsync<DevicePairException>(() => _resolver.PairAsync(device, "000000"));

        Assert.Contains("Failed: Wrong pairing code", ex.Message);
    }
}
