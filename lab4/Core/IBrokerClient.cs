namespace Labs.Lab4.Core;

/// How the publisher, the subscriber and the bench talk to a broker. Part 1 implements it with HttpClient
/// (HttpBrokerClient), Part 2 with a generated gRPC client (GrpcBrokerClient); the programs in ClientPrograms
/// are the same for both.
public interface IBrokerClient : IDisposable
{
    string Address { get; }

    /// Publishes one message; the answer is its id and time. Throws BrokerClientException.
    Task<Published> PublishAsync(string topic, string sender, string body, CancellationToken cancel);

    /// Subscribes to a topic. The first item comes when the broker has registered the subscriber: it has no message,
    /// only the last id of the topic at that moment. Then come the messages, in order, until the connection ends
    /// with a BrokerClientException. With afterId the broker first sends the stored messages after that id.
    IAsyncEnumerable<StreamItem> SubscribeAsync(string topic, long? afterId, CancellationToken cancel);

    Task<List<TopicInfo>> TopicsAsync(CancellationToken cancel);
}

/// The answer to a publish.
public sealed record Published(long Id, DateTimeOffset Time);

/// One item of a subscription: a message, or (the first item) only the last id of the topic.
public readonly record struct StreamItem(BrokerMessage? Message, long LastId);

/// A call to the broker that failed. Unreachable: the request never reached the broker (nothing is running at the address),
/// so sending it again cannot make a duplicate. Permanent: trying again will fail the same way (a 400, a 403).
public sealed class BrokerClientException(string message, bool unreachable = false, bool permanent = false, Exception? inner = null)
    : Exception(message, inner)
{
    public bool Unreachable { get; } = unreachable;
    public bool Permanent { get; } = permanent;
}
