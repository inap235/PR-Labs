using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Labs.Lab4.Core;
using Labs.Lab4.Grpc.Contract;
using Microsoft.Extensions.Hosting;

namespace Labs.Lab4.Grpc;

/// Part 2: the three calls of broker.proto, on the same MessageBroker as Part 1.
/// Kestrel runs every call on its own task, so many clients are served at the same time.
/// An error is an RpcException with a status code (the gRPC form of the HTTP status codes of Part 1).
public sealed class BrokerService(MessageBroker broker, IHostApplicationLifetime lifetime) : Contract.Broker.BrokerBase
{
    private readonly BrokerLog _log = broker.Log;

    /// Unary: saves and queues the message, and answers at once with its id, without waiting for the subscribers.
    public override Task<PublishReply> Publish(PublishRequest request, ServerCallContext context)
    {
        string client = Client(context);
        string? topic = null;
        try
        {
            topic = Topic(request.Topic);
            // Task 3: the token is in the metadata "authorization: Bearer <token>"
            var auth = broker.Authorize(context.RequestHeaders.GetValue("authorization"), topic);
            if (auth.Status == AuthStatus.Unauthorized)
                throw new RpcException(new Status(StatusCode.Unauthenticated,
                    $"publishing needs a known token in the metadata 'authorization: Bearer <token>' ({auth.Reason})"));
            if (auth.Status == AuthStatus.Forbidden)
                throw new RpcException(new Status(StatusCode.PermissionDenied, auth.Reason));

            string? sender = auth.Publisher ?? request.Sender;
            if (BrokerRules.CheckMessage(sender, request.Body) is { } problem)
                throw new RpcException(new Status(StatusCode.InvalidArgument, problem));

            BrokerMessage message;
            try
            {
                message = broker.Publish(topic, sender!.Trim(), request.Body);
            }
            catch (IOException e)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, $"the message could not be saved, it was not published ({e.Message})"));
            }
            _log.Write(client, "Publish", topic, message.Id, $"OK, from {message.Sender}");
            return Task.FromResult(new PublishReply { Id = message.Id, Time = Timestamp.FromDateTimeOffset(message.Time) });
        }
        catch (RpcException e)
        {
            _log.Write(client, "Publish", topic ?? request.Topic, null, $"{e.StatusCode}: {e.Status.Detail}");
            throw;
        }
        catch (Exception e)
        {
            _log.Write(client, "Publish", topic ?? request.Topic, null, $"Internal: {e.GetType().Name}: {e.Message}");
            throw new RpcException(new Status(StatusCode.Internal, "the broker could not handle the request"));
        }
    }

    /// Server streaming: the stream stays open, and every new message of the topic is written into it,
    /// until the subscriber goes away (the call is cancelled) or the broker stops.
    public override async Task Subscribe(SubscribeRequest request, IServerStreamWriter<Message> stream, ServerCallContext context)
    {
        string client = Client(context);
        string topic;
        try
        {
            topic = Topic(request.Topic);
            if (request.HasAfterId && request.AfterId < 0)
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"after_id must be 0 or more, got {request.AfterId}"));
        }
        catch (RpcException e)
        {
            _log.Write(client, "Subscribe", request.Topic, null, $"{e.StatusCode}: {e.Status.Detail}");
            throw;
        }

        var subscription = broker.Subscribe(topic, client, request.HasAfterId ? request.AfterId : null);
        // the call ends when the subscriber goes, when the broker drops it, or when the broker stops
        using var end = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, subscription.Dropped,
                                                                         lifetime.ApplicationStopping);
        BrokerMessage? writing = null; // the message being written, if a write fails
        try
        {
            // the headers go now, so the subscriber knows it is subscribed, and after which id
            await context.WriteResponseHeadersAsync(new Metadata { { "last-id", subscription.StartId.ToString() } });
            _log.Write(client, "Subscribe", topic, null, request.HasAfterId ? $"OK, subscribed after id {request.AfterId}" : "OK, subscribed");

            // no heartbeat of our own: HTTP/2 keep-alive pings (set in Program.cs) find a dead connection
            await foreach (var message in subscription.ReadAsync(Timeout.InfiniteTimeSpan, end.Token))
            {
                if (message is null)
                    continue;
                writing = message;
                await stream.WriteAsync(ToProto(message), end.Token);
                _log.Write(client, "DELIVER", topic, message.Id, "delivered");
                writing = null;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException)
        {
            if (!lifetime.ApplicationStopping.IsCancellationRequested)
                _log.Write(client, writing is null ? "Subscribe" : "DELIVER", topic, writing?.Id,
                           $"{(writing is null ? "ended" : "failed")}, the subscriber is gone and forgotten ({e.GetType().Name}: {e.Message})");
        }
        finally
        {
            broker.Unsubscribe(subscription);
        }
    }

    public override Task<ListTopicsReply> ListTopics(ListTopicsRequest request, ServerCallContext context)
    {
        var reply = new ListTopicsReply();
        reply.Topics.AddRange(broker.Topics().Select(t => new Contract.TopicInfo
        {
            Name = t.Name, Size = t.Size, LastId = t.LastId, Subscribers = t.Subscribers
        }));
        _log.Write(Client(context), "ListTopics", null, null, $"OK, {reply.Topics.Count} topics");
        return Task.FromResult(reply);
    }

    private static string Topic(string name) =>
        BrokerRules.NormalizeTopic(name)
        ?? throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{name}' is not a valid topic: {BrokerRules.TopicHelp}"));

    public static Message ToProto(BrokerMessage m) => new()
    {
        Id = m.Id, Topic = m.Topic, Sender = m.Sender, Body = m.Body, Time = Timestamp.FromDateTimeOffset(m.Time)
    };

    /// "ipv4:192.168.1.5:53122" -> "192.168.1.5:53122", like the endpoints in the log of Part 1.
    private static string Client(ServerCallContext context)
    {
        string peer = context.Peer ?? "?";
        int colon = peer.IndexOf(':');
        return colon > 0 && peer[..colon] is "ipv4" or "ipv6" ? Uri.UnescapeDataString(peer[(colon + 1)..]) : peer;
    }
}
