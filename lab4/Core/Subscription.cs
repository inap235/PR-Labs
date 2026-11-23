using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Labs.Lab4.Core;

/// One subscriber of a topic, while its connection is open.
/// The delivery task puts messages in its outbox; the connection of the subscriber (an HTTP answer or a gRPC stream)
/// takes them out and writes them to the network. So a slow subscriber never makes the delivery wait:
/// if its outbox is full, it is dropped instead.
public sealed class Subscription
{
    private readonly Channel<BrokerMessage> _outbox =
        Channel.CreateBounded<BrokerMessage>(new BoundedChannelOptions(BrokerRules.OutboxSize) { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _dropped = new();
    private List<BrokerMessage> _backlog = [];
    private long _lastSent;

    public Subscription(long id, string topic, string client)
    {
        Id = id;
        Topic = topic;
        Client = client;
    }

    public long Id { get; }
    public string Topic { get; }
    public string Client { get; }

    /// Cancelled when the broker drops this subscriber, so the connection can stop a write that hangs.
    public CancellationToken Dropped => _dropped.Token;

    /// Called once, after the subscriber is registered: the messages it asked for that are already stored,
    /// and the last id it has (so a message that comes both from the backlog and from the delivery is sent once).
    internal void Begin(long lastId, List<BrokerMessage> backlog)
    {
        _lastSent = lastId;
        StartId = lastId;
        _backlog = backlog;
    }

    /// The subscriber gets the messages after this id; it is told so, to ask for the messages after it if it has to connect again.
    public long StartId { get; private set; }

    /// Called by the delivery task; false if the outbox is full.
    internal bool Offer(BrokerMessage message) => _outbox.Writer.TryWrite(message);

    internal void Drop(string reason)
    {
        _outbox.Writer.TryComplete(new IOException(reason));
        _dropped.Cancel();
    }

    internal void Close() => _outbox.Writer.TryComplete();

    /// The messages to write to this subscriber, in order and each once: first the backlog, then the new ones.
    /// Gives null when nothing came for idle, so the connection can send a heartbeat (Timeout.InfiniteTimeSpan: never).
    public async IAsyncEnumerable<BrokerMessage?> ReadAsync(TimeSpan idle, [EnumeratorCancellation] CancellationToken cancel)
    {
        foreach (var message in _backlog)
        {
            if (message.Id <= _lastSent)
                continue;
            _lastSent = message.Id;
            yield return message;
        }
        _backlog = [];

        var reader = _outbox.Reader;
        while (true)
        {
            bool? ready = await WaitAsync(reader, idle, cancel);
            if (ready is null)
            {
                yield return null; // nothing for a while: heartbeat
                continue;
            }
            if (ready == false)
                yield break; // the broker is stopping

            while (reader.TryRead(out var message))
            {
                if (message.Id <= _lastSent)
                    continue; // already sent from the backlog
                _lastSent = message.Id;
                yield return message;
            }
        }
    }

    /// true: a message is waiting; false: the outbox is closed; null: nothing came for idle.
    private static async Task<bool?> WaitAsync(ChannelReader<BrokerMessage> reader, TimeSpan idle, CancellationToken cancel)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timer.CancelAfter(idle);
        try
        {
            return await reader.WaitToReadAsync(timer.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return null;
        }
    }
}
