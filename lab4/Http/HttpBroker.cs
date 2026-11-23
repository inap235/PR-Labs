using System.Net;
using System.Text;
using System.Text.Json;
using Labs.Lab4.Core;

namespace Labs.Lab4.Http;

/// Part 1: the broker as an HTTP server, on HttpListener of the standard library (no web framework).
///   POST /topics/{topic}/messages   queues a message          202 Accepted
///   GET  /topics/{topic}/stream     new messages, as NDJSON   200 OK (the answer stays open)
///   GET  /topics                    the topics and their sizes 200 OK
/// Every request is handled on its own task, so many clients are served at the same time.
public sealed class HttpBroker(MessageBroker broker, int port)
{
    private static readonly byte[] Heartbeat = "\n"u8.ToArray();
    private readonly BrokerLog _log = broker.Log;

    public async Task RunAsync(CancellationToken stop)
    {
        using var listener = StartListener();
        await using var stopListening = stop.Register(listener.Stop);
        var delivery = Task.Run(() => broker.DeliverAsync(stop));

        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                break; // Stop() ends the wait with an exception
            }
            catch (HttpListenerException e)
            {
                _log.Info($"could not accept a request: {e.Message}");
                continue;
            }
            _ = Task.Run(() => HandleAsync(context, stop));
        }
        await delivery;
    }

    /// http://+:port/ accepts requests from other computers too. On Windows that needs a URL reservation
    /// (netsh http add urlacl, once, as administrator); without it, only this computer can connect.
    private HttpListener StartListener()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        try
        {
            listener.Start();
            _log.Info($"listening on http://+:{port}/ (every network interface)");
            return listener;
        }
        catch (HttpListenerException e) when (e.ErrorCode == 5) // access denied
        {
            listener.Close();
        }

        listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _log.Info($"listening on http://localhost:{port}/ only: other computers cannot connect");
        Console.WriteLine($"""
              To accept other computers, run once in a terminal opened as administrator:
                netsh http add urlacl url=http://+:{port}/ user=Everyone
              and allow the port in the firewall:
                netsh advfirewall firewall add rule name="Lab4 broker" dir=in action=allow protocol=TCP localport={port}
            """);
        return listener;
    }

    /// One request. Every error is answered, logged, and never leaves this method: the broker does not crash because of a request.
    private async Task HandleAsync(HttpListenerContext context, CancellationToken stop)
    {
        var request = context.Request;
        var response = context.Response;
        string client = request.RemoteEndPoint?.ToString() ?? "?";
        string what = $"{request.HttpMethod} {request.Url?.AbsolutePath}";
        string? topic = null;
        try
        {
            string[] path = (request.Url?.AbsolutePath ?? "/").Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
            switch (path)
            {
                case ["topics"]:
                    if (request.HttpMethod != "GET")
                        throw new HttpError(405, "use GET /topics", allow: "GET");
                    await ListTopicsAsync(response);
                    _log.Write(client, what, null, null, "200 OK");
                    break;

                case ["topics", var name, "messages"]:
                    if (request.HttpMethod != "POST")
                        throw new HttpError(405, "use POST to publish", allow: "POST");
                    topic = Topic(name);
                    await PublishAsync(request, response, client, what, topic);
                    break;

                case ["topics", var name, "stream"]:
                    if (request.HttpMethod != "GET")
                        throw new HttpError(405, "use GET to subscribe", allow: "GET");
                    topic = Topic(name);
                    await StreamAsync(request, response, client, what, topic, stop);
                    break;

                default:
                    throw new HttpError(404, "no such resource: the API is POST /topics/{topic}/messages, GET /topics/{topic}/stream, GET /topics");
            }
        }
        catch (HttpError e)
        {
            _log.Write(client, what, topic, null, $"{e.Status} {Reason(e.Status)}: {e.Message}");
            if (e.Allow != null)
                response.Headers["Allow"] = e.Allow;
            if (e.Status == 401)
                response.Headers["WWW-Authenticate"] = "Bearer";
            await TryAnswerAsync(response, e.Status, new { error = e.Message });
        }
        catch (Exception e)
        {
            _log.Write(client, what, topic, null, $"500 Internal Server Error: {e.GetType().Name}: {e.Message}");
            await TryAnswerAsync(response, 500, new { error = "the broker could not handle the request" });
        }
    }

    private static string Topic(string name) =>
        BrokerRules.NormalizeTopic(name) ?? throw new HttpError(400, $"'{name}' is not a valid topic: {BrokerRules.TopicHelp}");

    /// POST /topics/{topic}/messages with {"sender": "...", "body": "..."}: answers 202 and the message (with id, topic, time)
    /// as soon as the message is saved and queued, without waiting for the subscribers.
    private async Task PublishAsync(HttpListenerRequest request, HttpListenerResponse response, string client, string what, string topic)
    {
        // Task 3: the token first, then the topic it allows
        var auth = broker.Authorize(request.Headers["Authorization"], topic);
        if (auth.Status == AuthStatus.Unauthorized)
            throw new HttpError(401, $"publishing needs a known token in 'Authorization: Bearer <token>' ({auth.Reason})");
        if (auth.Status == AuthStatus.Forbidden)
            throw new HttpError(403, auth.Reason);

        var body = await ReadJsonAsync(request);
        // with tokens, the sender is the publisher of the token: nobody can publish under another name
        string? sender = auth.Publisher ?? body.Sender;
        if (BrokerRules.CheckMessage(sender, body.Body) is { } problem)
            throw new HttpError(400, problem);

        BrokerMessage message;
        try
        {
            message = broker.Publish(topic, sender!.Trim(), body.Body!);
        }
        catch (IOException e)
        {
            throw new HttpError(503, $"the message could not be saved, it was not published ({e.Message})");
        }
        _log.Write(client, what, topic, message.Id, $"202 Accepted, from {message.Sender}");
        await AnswerAsync(response, 202, message);
    }

    private static async Task<PublishBody> ReadJsonAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 > BrokerRules.MaxRequestBytes)
            throw new HttpError(413, $"the body is larger than {BrokerRules.MaxRequestBytes} bytes");
        // read at most one byte more than allowed, so a chunked body without a length cannot be endless
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > BrokerRules.MaxRequestBytes)
                throw new HttpError(413, $"the body is larger than {BrokerRules.MaxRequestBytes} bytes");
        }
        try
        {
            return JsonSerializer.Deserialize<PublishBody>(buffer.ToArray(), BrokerRules.Json)
                   ?? throw new HttpError(400, "the body must be a JSON object: {\"sender\": \"...\", \"body\": \"...\"}");
        }
        catch (JsonException e)
        {
            throw new HttpError(400, $"the body is not valid JSON: {e.Message}");
        }
    }

    /// GET /topics/{topic}/stream[?after=id]: an answer that stays open, one JSON message per line (NDJSON), sent in chunks.
    /// An empty line is sent after 15 s of silence. When a write fails the subscriber is gone, and the broker forgets it.
    private async Task StreamAsync(HttpListenerRequest request, HttpListenerResponse response, string client, string what,
                                   string topic, CancellationToken stop)
    {
        long? after = null;
        if (request.QueryString["after"] is { } text)
        {
            if (!long.TryParse(text, out long value) || value < 0)
                throw new HttpError(400, $"after must be a message id (0 or more), got '{text}'");
            after = value;
        }

        var subscription = broker.Subscribe(topic, client, after);
        BrokerMessage? writing = null; // the message being written, if a write fails
        try
        {
            response.StatusCode = 200;
            response.ContentType = "application/x-ndjson; charset=utf-8";
            response.SendChunked = true;
            response.Headers["Cache-Control"] = "no-cache";
            response.Headers["Last-Id"] = subscription.StartId.ToString(); // the subscriber gets the messages after this id
            var output = response.OutputStream;
            // a first empty line sends the headers now: the subscriber knows it is subscribed
            await output.WriteAsync(Heartbeat, stop);
            _log.Write(client, what, topic, null, after is null ? "200 OK, subscribed" : $"200 OK, subscribed after id {after}");

            // a write that hangs (the subscriber stopped reading) is ended when the delivery drops the subscriber
            await using var abortWhenDropped = subscription.Dropped.Register(response.Abort);
            await foreach (var message in subscription.ReadAsync(BrokerRules.Heartbeat, stop))
            {
                writing = message;
                byte[] line = message is null
                    ? Heartbeat
                    : Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, BrokerRules.Json) + "\n");
                await output.WriteAsync(line, stop);
                await output.FlushAsync(stop);
                if (message != null)
                    _log.Write(client, "DELIVER", topic, message.Id, "delivered");
                writing = null;
            }
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException
                                      or OperationCanceledException)
        {
            if (!stop.IsCancellationRequested)
                _log.Write(client, writing is null ? what : "DELIVER", topic, writing?.Id,
                           $"{(writing is null ? "ended" : "failed")}, the subscriber is gone and forgotten ({e.GetType().Name}: {e.Message})");
        }
        finally
        {
            broker.Unsubscribe(subscription);
            try
            {
                response.Close();
            }
            catch (Exception)
            {
                // the connection is already broken
            }
        }
    }

    private async Task ListTopicsAsync(HttpListenerResponse response) =>
        await AnswerAsync(response, 200, new { topics = broker.Topics() });

    private static async Task AnswerAsync(HttpListenerResponse response, int status, object body)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, BrokerRules.Json);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    /// Answers an error; if even that fails (the client is gone), there is nobody left to answer.
    private static async Task TryAnswerAsync(HttpListenerResponse response, int status, object body)
    {
        try
        {
            await AnswerAsync(response, status, body);
        }
        catch (Exception)
        {
            try { response.Abort(); } catch (Exception) { /* already closed */ }
        }
    }

    private static string Reason(int status) => status switch
    {
        400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
        405 => "Method Not Allowed", 413 => "Content Too Large", 503 => "Service Unavailable", _ => ""
    };

    /// A request that the broker does not accept: its status code and why.
    private sealed class HttpError(int status, string message, string? allow = null) : Exception(message)
    {
        public int Status { get; } = status;
        public string? Allow { get; } = allow;
    }
}
