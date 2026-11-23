using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;
using Labs.Lab4.Core;
using Labs.Lab4.Grpc.Contract;
using TopicInfo = Labs.Lab4.Core.TopicInfo;

namespace Labs.Lab4.Grpc;

/// Part 2: the client side, on the client class generated from broker.proto.
/// A call looks like a method call; its errors are RpcExceptions with a status code.
public sealed class GrpcBrokerClient : IBrokerClient
{
    private readonly GrpcChannel _channel;
    private readonly Contract.Broker.BrokerClient _client;
    private readonly Metadata? _headers;

    public GrpcBrokerClient(Uri broker, string? token)
    {
        _channel = GrpcChannel.ForAddress(broker, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                // a ping every 15 s on an idle connection: a broker that is gone is noticed within 45 s
                KeepAlivePingDelay = TimeSpan.FromSeconds(15),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                EnableMultipleHttp2Connections = true
            }
        });
        _client = new Contract.Broker.BrokerClient(_channel);
        if (token != null)
            _headers = new Metadata { { "authorization", $"Bearer {token}" } };
        Address = broker.ToString().TrimEnd('/');
    }

    public string Address { get; }

    public async Task<Published> PublishAsync(string topic, string sender, string body, CancellationToken cancel)
    {
        try
        {
            var reply = await _client.PublishAsync(new PublishRequest { Topic = topic, Sender = sender, Body = body },
                                                   _headers, DateTime.UtcNow.AddSeconds(10), cancel);
            return new Published(reply.Id, reply.Time.ToDateTimeOffset());
        }
        catch (RpcException e)
        {
            throw Error(e);
        }
    }

    public async IAsyncEnumerable<StreamItem> SubscribeAsync(string topic, long? afterId, [EnumeratorCancellation] CancellationToken cancel)
    {
        var request = new SubscribeRequest { Topic = topic };
        if (afterId is { } after)
            request.AfterId = after;
        using var call = _client.Subscribe(request, cancellationToken: cancel);

        Metadata headers;
        try
        {
            headers = await call.ResponseHeadersAsync; // sent by the broker once the subscriber is registered
        }
        catch (RpcException e)
        {
            throw Error(e);
        }
        long lastId = long.TryParse(headers.GetValue("last-id"), out long id) ? id : 0;
        yield return new StreamItem(null, lastId);

        while (true)
        {
            bool more;
            try
            {
                more = await call.ResponseStream.MoveNext(cancel);
            }
            catch (RpcException e) when (e.StatusCode == StatusCode.Cancelled && cancel.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancel);
            }
            catch (RpcException e)
            {
                throw Error(e);
            }
            if (!more)
                throw new BrokerClientException("the broker ended the stream");
            var m = call.ResponseStream.Current;
            yield return new StreamItem(new BrokerMessage(m.Id, m.Topic, m.Sender, m.Body, m.Time.ToDateTimeOffset()), m.Id);
        }
    }

    public async Task<List<TopicInfo>> TopicsAsync(CancellationToken cancel)
    {
        try
        {
            var reply = await _client.ListTopicsAsync(new ListTopicsRequest(), deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancel);
            return reply.Topics.Select(t => new TopicInfo(t.Name, t.Size, t.LastId, t.Subscribers)).ToList();
        }
        catch (RpcException e)
        {
            throw Error(e);
        }
    }

    /// Unavailable: no connection to the broker (it is not running, or restarting), so the call did not reach it.
    /// InvalidArgument, Unauthenticated, PermissionDenied: the request is wrong, trying again cannot help.
    private static BrokerClientException Error(RpcException e) => new(
        $"{e.StatusCode}: {e.Status.Detail}",
        unreachable: e.StatusCode == StatusCode.Unavailable,
        permanent: e.StatusCode is StatusCode.InvalidArgument or StatusCode.Unauthenticated or StatusCode.PermissionDenied,
        inner: e);

    public void Dispose() => _channel.Dispose();
}
