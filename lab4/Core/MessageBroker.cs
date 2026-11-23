using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Labs.Lab4.Core;

/// The broker without its network part, the same in Part 1 (HTTP) and Part 2 (gRPC).
/// Publishes are the producers: they put messages in the queue. The delivery task is the consumer:
/// it takes them out in order and gives each one to every subscriber of its topic.
public sealed class MessageBroker
{
    private readonly Channel<BrokerMessage> _queue =
        Channel.CreateUnbounded<BrokerMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<long, Subscription> _subscribers = new();
    private long _lastSubscriber;

    public MessageBroker(MessageStore store, TokenFile? tokens, BrokerLog log)
    {
        Store = store;
        Tokens = tokens;
        Log = log;
    }

    public MessageStore Store { get; }
    public TokenFile? Tokens { get; }
    public BrokerLog Log { get; }

    /// Task 3: who may publish to the topic. Without a token file (Tasks 1 and 2) everybody may.
    public AuthResult Authorize(string? authorization, string topic)
    {
        if (Tokens is null)
            return new AuthResult(AuthStatus.Allowed, null, "no token file");
        var publisher = Tokens.Identify(authorization);
        if (publisher is null)
            return new AuthResult(AuthStatus.Unauthorized, null, authorization is null ? "no token" : "unknown token");
        if (!publisher.MayPublish(topic))
            return new AuthResult(AuthStatus.Forbidden, publisher.Name, $"{publisher.Name} may not publish to '{topic}'");
        return new AuthResult(AuthStatus.Allowed, publisher.Name, publisher.Name);
    }

    /// Stores the message (with its id and time) and puts it in the queue; does not wait for the subscribers.
    /// Throws IOException if the message cannot be saved.
    public BrokerMessage Publish(string topic, string sender, string body) =>
        Store.Add(topic, sender, body, message => _queue.Writer.TryWrite(message));

    /// Registers a subscriber of the topic. With afterId it first gets the stored messages after that id
    /// (a subscriber that comes back after a restart), otherwise only the messages published from now on.
    public Subscription Subscribe(string topic, string client, long? afterId)
    {
        var subscription = new Subscription(Interlocked.Increment(ref _lastSubscriber), topic, client);
        // Registered first, read second: a message delivered in between is in both, and is sent once;
        // a message cannot be in neither.
        _subscribers[subscription.Id] = subscription;
        var (lastId, backlog) = Store.Read(topic, afterId);
        long from = afterId is { } after ? Math.Min(after, lastId) : lastId;
        subscription.Begin(from, backlog);
        return subscription;
    }

    /// The broker forgets a subscriber when its connection ends.
    public void Unsubscribe(Subscription subscription) => _subscribers.TryRemove(subscription.Id, out _);

    public List<TopicInfo> Topics()
    {
        var counts = _subscribers.Values.GroupBy(s => s.Topic).ToDictionary(g => g.Key, g => g.Count());
        var rows = Store.Sizes().Select(t => new TopicInfo(t.Name, t.Size, t.LastId, counts.GetValueOrDefault(t.Name))).ToList();
        // topics that have subscribers but no messages yet
        rows.AddRange(counts.Where(c => rows.All(r => r.Name != c.Key)).Select(c => new TopicInfo(c.Key, 0, 0, c.Value)));
        return rows.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
    }

    /// The delivery task, in the background for as long as the broker runs.
    public async Task DeliverAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(stop))
            {
                foreach (var subscription in _subscribers.Values)
                {
                    if (subscription.Topic != message.Topic || subscription.Offer(message))
                        continue;
                    Unsubscribe(subscription);
                    subscription.Drop($"more than {BrokerRules.OutboxSize} messages behind");
                    Log.Write(subscription.Client, "DELIVER", message.Topic, message.Id,
                              $"dropped: the subscriber is more than {BrokerRules.OutboxSize} messages behind");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // the broker is stopping
        }
        foreach (var subscription in _subscribers.Values)
            subscription.Close();
    }
}

public enum AuthStatus { Allowed, Unauthorized, Forbidden }

/// The answer of Authorize: the status, the name of the publisher (if the token is known) and why.
public sealed record AuthResult(AuthStatus Status, string? Publisher, string Reason);
