using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Labs.Lab4.Core;

namespace Labs.Lab4.Http;

/// Part 1: the client side of the HTTP API, on HttpClient of the standard library.
public sealed class HttpBrokerClient : IBrokerClient
{
    private readonly HttpClient _http;

    public HttpBrokerClient(Uri broker, string? token)
    {
        // no overall time limit: a subscription stays open for hours; publishes get their own limit below
        _http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
        {
            BaseAddress = broker,
            Timeout = Timeout.InfiniteTimeSpan
        };
        if (token != null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Address = broker.ToString().TrimEnd('/');
    }

    public string Address { get; }

    public async Task<Published> PublishAsync(string topic, string sender, string body, CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await Call(() => _http.PostAsJsonAsync($"topics/{Uri.EscapeDataString(topic)}/messages",
                                                                    new PublishBody(sender, body), BrokerRules.Json, limit.Token), cancel);
        if (response.StatusCode != HttpStatusCode.Accepted)
            throw await ErrorOf(response);
        var message = await Call(() => response.Content.ReadFromJsonAsync<BrokerMessage>(BrokerRules.Json, limit.Token), cancel)
                      ?? throw new BrokerClientException("the broker answered 202 without the message");
        return new Published(message.Id, message.Time);
    }

    public async IAsyncEnumerable<StreamItem> SubscribeAsync(string topic, long? afterId, [EnumeratorCancellation] CancellationToken cancel)
    {
        string url = $"topics/{Uri.EscapeDataString(topic)}/stream" + (afterId is { } after ? $"?after={after}" : "");
        // ResponseHeadersRead: return as soon as the headers arrive, and read the body as it comes
        using var response = await Call(() => _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel), cancel);
        if (response.StatusCode != HttpStatusCode.OK)
            throw await ErrorOf(response);
        long lastId = response.Headers.TryGetValues("Last-Id", out var values) && long.TryParse(values.First(), out long id) ? id : 0;
        using var reader = new StreamReader(await Call(() => response.Content.ReadAsStreamAsync(cancel), cancel));
        yield return new StreamItem(null, lastId);

        // the broker sends an empty line every 15 s: after three missing ones it is taken for dead
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        while (true)
        {
            silence.CancelAfter(BrokerRules.Heartbeat * 3);
            string? line;
            try
            {
                line = await reader.ReadLineAsync(silence.Token);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new BrokerClientException($"nothing from the broker for {(BrokerRules.Heartbeat * 3).TotalSeconds} s");
            }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                throw new BrokerClientException(e.Message, inner: e);
            }
            if (line is null)
                throw new BrokerClientException("the broker closed the stream");
            if (line.Length == 0)
                continue; // heartbeat

            BrokerMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<BrokerMessage>(line, BrokerRules.Json);
            }
            catch (JsonException e)
            {
                throw new BrokerClientException($"the broker sent a line that is not a message: {e.Message}");
            }
            if (message != null)
                yield return new StreamItem(message, message.Id);
        }
    }

    public async Task<List<TopicInfo>> TopicsAsync(CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await Call(() => _http.GetAsync("topics", limit.Token), cancel);
        if (response.StatusCode != HttpStatusCode.OK)
            throw await ErrorOf(response);
        var answer = await Call(() => response.Content.ReadFromJsonAsync<TopicsAnswer>(BrokerRules.Json, limit.Token), cancel);
        return answer?.Topics ?? [];
    }

    private sealed record TopicsAnswer(List<TopicInfo> Topics);

    /// Runs a call and turns what can go wrong on the network into a BrokerClientException.
    private static async Task<T> Call<T>(Func<Task<T>> call, CancellationToken cancel)
    {
        try
        {
            return await call();
        }
        catch (HttpRequestException e)
        {
            // ConnectionError: the connection could not be made, so the request was never sent
            throw new BrokerClientException(e.Message, unreachable: e.HttpRequestError == HttpRequestError.ConnectionError, inner: e);
        }
        catch (OperationCanceledException e) when (!cancel.IsCancellationRequested)
        {
            throw new BrokerClientException("the broker did not answer in time", inner: e);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            throw new BrokerClientException(e.Message, inner: e);
        }
    }

    /// An answer that is not a success: its status and the "error" of its JSON body.
    private static async Task<BrokerClientException> ErrorOf(HttpResponseMessage response)
    {
        string detail = "";
        try
        {
            string text = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(text);
            detail = json.RootElement.GetProperty("error").GetString() ?? "";
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IOException or HttpRequestException)
        {
            // no JSON error in the answer: the status says enough
        }
        int status = (int)response.StatusCode;
        // 4xx: the request itself is wrong, sending it again cannot help; 5xx: the broker may get better
        return new BrokerClientException($"{status} {response.ReasonPhrase}{(detail.Length > 0 ? ": " + detail : "")}",
                                         permanent: status is >= 400 and < 500);
    }

    public void Dispose() => _http.Dispose();
}
