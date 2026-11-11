using AdbSync.Server.Events;
using Microsoft.Extensions.Time.Testing;

namespace AdbSync.Server.Tests;

public class EventBusTests
{
    private static readonly IReadOnlySet<string> NoTopics = new HashSet<string>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _client = Guid.NewGuid();

    private static List<ServerEvent> Drain(EventBus.Subscription subscription)
    {
        var events = new List<ServerEvent>();
        while (subscription.Live.TryRead(out var evt))
            events.Add(evt);
        return events;
    }

    [Fact]
    public void Subscribe_WithLastEventId_ReplaysOnlyLaterEvents()
    {
        var bus = new EventBus(_clock);
        var first = bus.Publish("job.status", "a");
        bus.Publish("job.status", "b");
        bus.Publish("job.status", "c");

        using var subscription = bus.Subscribe(_client, first.Seq, NoTopics);

        Assert.Null(subscription.ResyncReason);
        Assert.Equal(["b", "c"], subscription.Replay.Select(e => (string)e.Data));
    }

    [Fact]
    public void Seq_IsStrictlyIncreasingAndStartsAboveAPreviousServerRun()
    {
        var earlier = new EventBus(_clock);
        var lastOfEarlierRun = Enumerable.Range(0, 5).Select(_ => earlier.Publish("job.status", "x")).Last();
        _clock.Advance(TimeSpan.FromSeconds(1));

        var restarted = new EventBus(_clock);
        var firstAfterRestart = restarted.Publish("job.status", "y");

        Assert.True(firstAfterRestart.Seq > lastOfEarlierRun.Seq);
    }

    [Fact]
    public void Subscribe_IdFromAnEarlierServerRun_AsksForResync()
    {
        var earlier = new EventBus(_clock);
        var old = earlier.Publish("job.status", "x");
        _clock.Advance(TimeSpan.FromSeconds(1));
        var bus = new EventBus(_clock);
        bus.Publish("job.status", "y");

        using var subscription = bus.Subscribe(_client, old.Seq, NoTopics);

        Assert.Equal("serverRestarted", subscription.ResyncReason);
        Assert.Empty(subscription.Replay);
    }

    [Fact]
    public void Subscribe_IdOlderThanTheBuffer_AsksForResync()
    {
        var bus = new EventBus(_clock);
        var first = bus.Publish("job.status", "x");
        bus.Publish("job.status", "y");
        _clock.Advance(EventBus.BufferAge + TimeSpan.FromMinutes(1));
        bus.Publish("job.status", "z");

        using var subscription = bus.Subscribe(_client, first.Seq, NoTopics);

        Assert.Equal("tooOld", subscription.ResyncReason);
    }

    [Fact]
    public void Subscribe_IdOfTheLastEvent_ResumesWithNothingMissing()
    {
        var bus = new EventBus(_clock);
        var last = bus.Publish("job.status", "x");

        using var subscription = bus.Subscribe(_client, last.Seq, NoTopics);
        bus.Publish("job.status", "y");

        Assert.Null(subscription.ResyncReason);
        Assert.Equal(["y"], Drain(subscription).Select(e => (string)e.Data));
    }

    [Fact]
    public void Publish_ForOneClient_OnlyReachesThatClient()
    {
        var bus = new EventBus(_clock);
        using var mine = bus.Subscribe(_client, null, NoTopics);
        using var other = bus.Subscribe(Guid.NewGuid(), null, NoTopics);

        bus.Publish("client.revoked", "bye", onlyClient: _client);

        Assert.Single(Drain(mine));
        Assert.Empty(Drain(other));
    }

    [Fact]
    public void Publish_TopicEvents_OnlyReachSubscribersThatOptedIn()
    {
        var bus = new EventBus(_clock);
        var runId = Guid.NewGuid();
        using var plain = bus.Subscribe(_client, null, NoTopics);
        using var oneRun = bus.Subscribe(_client, null, new HashSet<string> { $"run.log:{runId}" });
        using var allRuns = bus.Subscribe(_client, null, new HashSet<string> { "run.log:*" });

        bus.Publish("run.log", "line", topic: $"run.log:{runId}");
        bus.Publish("run.log", "other", topic: $"run.log:{Guid.NewGuid()}");

        Assert.Empty(Drain(plain));
        Assert.Equal(["line"], Drain(oneRun).Select(e => (string)e.Data));
        Assert.Equal(2, Drain(allRuns).Count);
    }

    [Fact]
    public async Task Disconnect_DeliversWhatWasQueuedThenEndsTheStream()
    {
        var bus = new EventBus(_clock);
        using var subscription = bus.Subscribe(_client, null, NoTopics);

        bus.Publish("client.revoked", "bye", onlyClient: _client);
        bus.Disconnect(_client);

        Assert.Single(Drain(subscription));
        Assert.False(await subscription.Live.WaitToReadAsync());
    }
}
