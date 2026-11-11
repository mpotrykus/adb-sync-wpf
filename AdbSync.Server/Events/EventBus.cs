using System.Threading.Channels;

namespace AdbSync.Server.Events;

public sealed record ServerEvent(long Seq, string Type, DateTimeOffset At, object Data, Guid? OnlyClient, string? Topic);

/// <summary>
/// The one publisher behind GET /events. Assigns <c>seq</c>, keeps a replay buffer (the last 2,000 events or 15 minutes,
/// whichever is smaller) and fans events out to connected streams.
/// <para>
/// <c>seq</c> starts at the server's start time in Unix milliseconds rather than 1, so a restarted server's numbers are
/// always above anything the previous run sent; a client resuming with an older <c>Last-Event-ID</c> then falls before
/// the buffer and is told to resync instead of silently missing events.
/// </para>
/// </summary>
public sealed class EventBus
{
    public const int BufferLimit = 2000;
    public static readonly TimeSpan BufferAge = TimeSpan.FromMinutes(15);
    private const int SubscriberQueueLimit = 1000;

    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly LinkedList<ServerEvent> _buffer = new();
    private readonly List<Subscription> _subscribers = [];
    private readonly long _firstSeq;
    private long _seq;

    public EventBus(TimeProvider clock)
    {
        _clock = clock;
        _firstSeq = clock.GetUtcNow().ToUnixTimeMilliseconds();
        _seq = _firstSeq - 1;
    }

    public long CurrentSeq
    {
        get { lock (_gate) return _seq; }
    }

    /// <summary>Publishes an event. <paramref name="onlyClient"/> limits it to one client; <paramref name="topic"/> makes it opt-in.</summary>
    public ServerEvent Publish(string type, object data, Guid? onlyClient = null, string? topic = null)
    {
        lock (_gate)
        {
            var evt = new ServerEvent(++_seq, type, _clock.GetUtcNow(), data, onlyClient, topic);
            _buffer.AddLast(evt);
            Trim();
            foreach (var subscriber in _subscribers.ToList())
            {
                if (subscriber.Wants(evt) && !subscriber.Writer.TryWrite(evt))
                    Remove(subscriber);
            }
            return evt;
        }
    }

    /// <summary>
    /// Starts a stream for <paramref name="clientId"/>. Events after <paramref name="lastSeq"/> are replayed first; if
    /// they're no longer buffered (or the number comes from another server run) <see cref="Subscription.ResyncReason"/>
    /// says so and nothing is replayed.
    /// </summary>
    public Subscription Subscribe(Guid clientId, long? lastSeq, IReadOnlySet<string> topics)
    {
        lock (_gate)
        {
            var subscription = new Subscription(this, clientId, topics, Channel.CreateBounded<ServerEvent>(
                new BoundedChannelOptions(SubscriberQueueLimit) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait }));
            Trim();
            if (lastSeq is { } last)
            {
                var oldestAvailable = _buffer.First?.Value.Seq ?? _seq + 1;
                if (last < _firstSeq - 1 || last > _seq)
                    subscription.ResyncReason = "serverRestarted";
                else if (last < oldestAvailable - 1)
                    subscription.ResyncReason = "tooOld";
                else
                    subscription.Replay = _buffer.Where(e => e.Seq > last && subscription.Wants(e)).ToList();
            }
            _subscribers.Add(subscription);
            return subscription;
        }
    }

    /// <summary>Ends every open stream of <paramref name="clientId"/>, for example after its token is revoked.</summary>
    public void Disconnect(Guid clientId)
    {
        lock (_gate)
        {
            foreach (var subscriber in _subscribers.Where(s => s.ClientId == clientId).ToList())
                Remove(subscriber);
        }
    }

    private void Remove(Subscription subscription)
    {
        _subscribers.Remove(subscription);
        subscription.Writer.TryComplete();
    }

    private void Trim()
    {
        var cutoff = _clock.GetUtcNow() - BufferAge;
        while (_buffer.First is { } first && (_buffer.Count > BufferLimit || first.Value.At < cutoff))
            _buffer.RemoveFirst();
    }

    public sealed class Subscription : IDisposable
    {
        private readonly EventBus _bus;
        private readonly Channel<ServerEvent> _channel;
        private readonly IReadOnlySet<string> _topics;

        internal Subscription(EventBus bus, Guid clientId, IReadOnlySet<string> topics, Channel<ServerEvent> channel)
        {
            _bus = bus;
            ClientId = clientId;
            _topics = topics;
            _channel = channel;
        }

        public Guid ClientId { get; }
        public string? ResyncReason { get; internal set; }
        public IReadOnlyList<ServerEvent> Replay { get; internal set; } = [];
        public ChannelReader<ServerEvent> Live => _channel.Reader;
        internal ChannelWriter<ServerEvent> Writer => _channel.Writer;

        internal bool Wants(ServerEvent evt)
        {
            if (evt.OnlyClient is { } only && only != ClientId)
                return false;
            if (evt.Topic is null)
                return true;
            var family = evt.Topic[..(evt.Topic.IndexOf(':') + 1)];
            return _topics.Contains(evt.Topic) || _topics.Contains(family + "*");
        }

        public void Dispose()
        {
            lock (_bus._gate)
                _bus.Remove(this);
        }
    }
}
