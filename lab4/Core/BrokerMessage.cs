namespace Labs.Lab4.Core;

/// One message of a topic: what the publisher sent (sender, body), plus what the broker adds (id, topic, time).
/// The ids of a topic are 1, 2, 3, ... with no gaps.
public sealed record BrokerMessage(long Id, string Topic, string Sender, string Body, DateTimeOffset Time);

/// What a publisher sends in Part 1: the broker adds the id, the topic and the time.
public sealed record PublishBody(string? Sender, string? Body);

/// One row of the list of topics.
public sealed record TopicInfo(string Name, long Size, long LastId, int Subscribers);
