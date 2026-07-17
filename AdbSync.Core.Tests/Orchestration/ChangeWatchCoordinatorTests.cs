using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Devices;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Transfer;
using AdbSync.Core.Tests.Orchestration.Fakes;
using NSubstitute;
using System.Collections.Concurrent;

namespace AdbSync.Core.Tests.Orchestration;

public class ChangeWatchCoordinatorTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromMilliseconds(30);
    private static readonly DeviceConfig Device = new() { Name = "Phone", Serial = "abc123" };
    private static readonly IExcludeMatcher NoExclude = new ExcludeMatcher([]);

    [Fact]
    public async Task RapidSignals_CoalesceIntoOneTrigger()
    {
        var watcher = new FakeDeviceChangeWatcher();
        var events = new RecordingSyncEventSink();
        var triggerCount = 0;

        await using var coordinator = new ChangeWatchCoordinator(
            "Job", [new ChangeWatchBinding(Device, "/sdcard/DCIM", NoExclude)], watcher, new FakeDeviceResolver(), events,
            debounceWindow: TimeSpan.FromMilliseconds(60), rescanInterval: TimeSpan.FromSeconds(30),
            onTriggered: () => { Interlocked.Increment(ref triggerCount); return Task.CompletedTask; },
            pollInterval: PollInterval, reconnectBackoff: ReconnectBackoff);

        coordinator.Start();
        await WaitUntilAsync(() => watcher.WatchAsyncCallCount >= 1);

        for (var i = 0; i < 5; i++)
        {
            watcher.EmitSignal();
            await Task.Delay(10);
        }

        await WaitUntilAsync(() => triggerCount >= 1, timeout: TimeSpan.FromSeconds(2));
        await Task.Delay(150);

        Assert.Equal(1, triggerCount);
        Assert.Contains(events.WatchStartedCalls, c => c is ("Job", "Phone", true));
    }

    [Fact]
    public async Task ChangeCausedByOwnTrigger_DoesNotReTrigger()
    {
        var watcher = new FakeDeviceChangeWatcher();
        var events = new RecordingSyncEventSink();
        var triggerCount = 0;

        await using var coordinator = new ChangeWatchCoordinator(
            "Job", [new ChangeWatchBinding(Device, "/sdcard/DCIM", NoExclude)], watcher, new FakeDeviceResolver(), events,
            debounceWindow: TimeSpan.FromMilliseconds(20), rescanInterval: TimeSpan.FromSeconds(30),
            onTriggered: () =>
            {
                Interlocked.Increment(ref triggerCount);
                watcher.EmitSignal();
                return Task.CompletedTask;
            },
            pollInterval: PollInterval, reconnectBackoff: ReconnectBackoff,
            postTriggerSuppression: TimeSpan.FromMilliseconds(80));

        coordinator.Start();
        await WaitUntilAsync(() => watcher.WatchAsyncCallCount >= 1, timeout: TimeSpan.FromSeconds(2));

        watcher.EmitSignal();

        await WaitUntilAsync(() => triggerCount >= 1, timeout: TimeSpan.FromSeconds(2));
        await Task.Delay(200);

        Assert.Equal(1, triggerCount);
    }

    [Fact]
    public async Task UnavailableInotifyd_FallsBackToPolling_AndDetectsChange()
    {
        var watcher = new FakeDeviceChangeWatcher
        {
            Availability = new(false, "inotifyd not present on this device's shell"),
            SnapshotSequence = call => call == 0 ? "v1" : "v2",
        };
        var events = new RecordingSyncEventSink();
        var triggerCount = 0;

        await using var coordinator = new ChangeWatchCoordinator(
            "Job", [new ChangeWatchBinding(Device, "/sdcard/Android/data/com.foo", NoExclude)], watcher, new FakeDeviceResolver(), events,
            debounceWindow: TimeSpan.FromMilliseconds(20), rescanInterval: TimeSpan.FromSeconds(30),
            onTriggered: () => { Interlocked.Increment(ref triggerCount); return Task.CompletedTask; },
            pollInterval: PollInterval, reconnectBackoff: ReconnectBackoff);

        coordinator.Start();

        await WaitUntilAsync(() => events.WatchDegradedCalls.Count >= 1, timeout: TimeSpan.FromSeconds(2));
        Assert.Contains(events.WatchDegradedCalls, c => c.JobName == "Job" && c.Reason.Contains("inotifyd"));

        await WaitUntilAsync(() => triggerCount >= 1, timeout: TimeSpan.FromSeconds(2));
        Assert.Contains(events.ChangeDetectedCalls, c => c is ("Job", "Phone"));
    }

    [Fact]
    public async Task StreamFailure_ReconnectsInsteadOfDying()
    {
        var watcher = new FakeDeviceChangeWatcher
        {
            ThrowOnWatchCall = callIndex => callIndex == 0,
        };
        var events = new RecordingSyncEventSink();
        var resolveCalls = new ConcurrentBag<DateTime>();
        var resolver = Substitute.For<IAdbDeviceResolver>();
        resolver.EnsureConnectedAsync(Arg.Any<DeviceConfig>(), Arg.Any<CancellationToken>())
            .Returns(_ => { resolveCalls.Add(DateTime.UtcNow); return Task.FromResult("abc123"); });

        var triggerCount = 0;
        await using var coordinator = new ChangeWatchCoordinator(
            "Job", [new ChangeWatchBinding(Device, "/sdcard/DCIM", NoExclude)], watcher, resolver, events,
            debounceWindow: TimeSpan.FromMilliseconds(20), rescanInterval: TimeSpan.FromSeconds(30),
            onTriggered: () => { Interlocked.Increment(ref triggerCount); return Task.CompletedTask; },
            pollInterval: PollInterval, reconnectBackoff: ReconnectBackoff);

        coordinator.Start();

        await WaitUntilAsync(() => watcher.WatchAsyncCallCount >= 2, timeout: TimeSpan.FromSeconds(2));
        Assert.True(resolveCalls.Count >= 2, "expected the coordinator to re-resolve the device after the stream failure");

        await WaitUntilAsync(() => watcher.WatchAsyncCallCount >= 2 && watcher.CheckAvailabilityCallCount >= 2);
        watcher.EmitSignal();

        await WaitUntilAsync(() => triggerCount >= 1, timeout: TimeSpan.FromSeconds(2));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(1));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
