using AdbSync.Core.Services.Orchestration;

namespace AdbSync.Core.Tests.Orchestration;

public class DeviceAccessGateTests
{
    [Fact]
    public async Task AcquireAsync_DifferentDeviceNames_BothAcquireImmediately()
    {
        var gate = new DeviceAccessGate();

        var handleA = await gate.AcquireAsync("DeviceA", 1);
        var acquireB = gate.AcquireAsync("DeviceB", 1);

        var completed = await Task.WhenAny(acquireB, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(acquireB, completed);

        await handleA.DisposeAsync();
        await (await acquireB).DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_SameDeviceNameHeldByAnotherCaller_WaitsUntilReleased()
    {
        var gate = new DeviceAccessGate();
        var handleA = await gate.AcquireAsync("DeviceA", 1);

        var secondAcquire = gate.AcquireAsync("DeviceA", 1);
        var wonRace = await Task.WhenAny(secondAcquire, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(secondAcquire, wonRace);

        await handleA.DisposeAsync();

        var handle = await secondAcquire;
        Assert.NotNull(handle);
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_IsCaseInsensitiveOnDeviceName()
    {
        var gate = new DeviceAccessGate();
        var handle = await gate.AcquireAsync("DeviceA", 1);

        var secondAcquire = gate.AcquireAsync("deviceA", 1);
        var wonRace = await Task.WhenAny(secondAcquire, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(secondAcquire, wonRace);

        await handle.DisposeAsync();
        await (await secondAcquire).DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_MaxConcurrentTwo_SecondCallerAcquiresImmediatelyThirdWaits()
    {
        var gate = new DeviceAccessGate();

        var handleA = await gate.AcquireAsync("DeviceA", 2);
        var acquireB = gate.AcquireAsync("DeviceA", 2);
        var completedB = await Task.WhenAny(acquireB, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(acquireB, completedB);
        var handleB = await acquireB;

        var thirdAcquire = gate.AcquireAsync("DeviceA", 2);
        var wonRace = await Task.WhenAny(thirdAcquire, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(thirdAcquire, wonRace);

        await handleA.DisposeAsync();
        var handleC = await thirdAcquire;

        await handleB.DisposeAsync();
        await handleC.DisposeAsync();
    }
}
