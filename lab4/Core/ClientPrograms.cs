using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Labs.Lab4.Core;

/// The publisher, the subscriber, the list of topics and the bench, written once for both parts:
/// they only talk to the broker through an IBrokerClient (HTTP in Part 1, gRPC in Part 2).
public static class ClientPrograms
{
    /// Publishes every line that the user types. A message that cannot reach the broker (it is restarting)
    /// is tried again every second for up to 30 s; any other error is shown and the next line is read.
    public static async Task<int> PublishAsync(IBrokerClient client, string topic, string name)
    {
        Console.WriteLine($"Publishing to [{topic}] as {name} on {client.Address}");
        Console.WriteLine("Type a message and press Enter. /quit (or Ctrl+Z, Enter) stops.");
        while (Console.ReadLine() is { } typed)
        {
            string line = typed.TrimStart('﻿'); // the byte order mark that some shells put before piped UTF-8 text
            if (line.Trim() == "/quit")
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    var published = await client.PublishAsync(topic, name, line, CancellationToken.None);
                    Console.WriteLine($"  -> #{published.Id} at {published.Time.ToLocalTime():HH:mm:ss.fff}");
                    break;
                }
                catch (BrokerClientException e) when (e.Unreachable && attempt < 30)
                {
                    if (attempt == 1)
                        Console.WriteLine($"  the broker is not reachable ({e.Message.TrimEnd('.')}), trying again every second...");
                    await Task.Delay(1000);
                }
                catch (BrokerClientException e)
                {
                    Console.WriteLine($"  not published: {e.Message}");
                    break;
                }
            }
        }
        return 0;
    }

    /// Shows each message as it arrives: [grades] teacher: Lab 3 grades are online.
    /// When the connection is lost (the broker restarts), it connects again and asks for the messages after
    /// the last one it showed, so it misses none and shows none twice.
    public static async Task<int> SubscribeAsync(IBrokerClient client, string topic, long? afterId, bool showIds, CancellationToken cancel)
    {
        long? last = afterId;
        bool connected = false;
        int delay = 1;
        while (!cancel.IsCancellationRequested)
        {
            bool streaming = false; // this attempt got as far as the subscription
            try
            {
                await foreach (var item in client.SubscribeAsync(topic, last, cancel))
                {
                    if (item.Message is not { } message)
                    {
                        streaming = true;
                        Console.WriteLine(connected
                            ? $"(connected again to {client.Address})"
                            : $"Subscribed to [{topic}] on {client.Address}. Ctrl+C stops.");
                        if (last is null || item.LastId < last)
                            last = item.LastId; // from now on; or the broker lost messages: go on from what it has
                        connected = true;
                        delay = 1;
                        continue;
                    }
                    if (message.Id <= last)
                        continue;
                    if (message.Id > last + 1)
                        Console.WriteLine($"(messages {last + 1} to {message.Id - 1} were not received)");
                    last = message.Id;
                    Console.WriteLine(Format(message, showIds));
                }
            }
            catch (BrokerClientException e) when (!e.Permanent)
            {
                Console.WriteLine($"({(streaming ? "connection lost" : "cannot connect")}: {e.Message.TrimEnd('.')}; trying again in {delay} s)");
            }
            catch (BrokerClientException e)
            {
                Console.Error.WriteLine($"Error: {e.Message}");
                return 1;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), cancel);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            delay = Math.Min(delay * 2, 5);
        }
        return 0;
    }

    public static string Format(BrokerMessage message, bool showIds) =>
        showIds
            ? $"#{message.Id} {message.Time.ToLocalTime():HH:mm:ss} [{message.Topic}] {message.Sender}: {message.Body}"
            : $"[{message.Topic}] {message.Sender}: {message.Body}";

    public static async Task<int> TopicsAsync(IBrokerClient client)
    {
        try
        {
            var topics = await client.TopicsAsync(CancellationToken.None);
            Console.WriteLine($"{"topic",-24} {"messages",9} {"last id",8} {"subscribers",12}");
            foreach (var t in topics)
                Console.WriteLine($"{t.Name,-24} {t.Size,9} {t.LastId,8} {t.Subscribers,12}");
            if (topics.Count == 0)
                Console.WriteLine("(no topics yet)");
            return 0;
        }
        catch (BrokerClientException e)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    /// Measures the broker and checks Task 1 under load: a subscriber, then several publishers at the same time.
    /// Every id must be given once, with no gaps, and the subscriber must get every message once, in order.
    /// Prints a summary, and a line "BENCH {json}" for the tests and the report.
    public static async Task<int> BenchAsync(IBrokerClient client, string topic, int messages, int publishers)
    {
        using var stop = new CancellationTokenSource();
        var subscribed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = new ConcurrentDictionary<long, long>(); // id -> Stopwatch ticks
        var order = new List<long>();
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long firstId = 0;

        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in client.SubscribeAsync(topic, null, stop.Token))
                {
                    if (item.Message is not { } m)
                    {
                        firstId = item.LastId + 1;
                        subscribed.TrySetResult(item.LastId);
                        continue;
                    }
                    long now = Stopwatch.GetTimestamp();
                    arrived.TryAdd(m.Id, now);
                    order.Add(m.Id);
                    if (order.Count >= messages)
                        all.TrySetResult();
                }
            }
            catch (Exception e) when (e is OperationCanceledException or BrokerClientException)
            {
                subscribed.TrySetException(e);
                all.TrySetException(e);
            }
        });

        try
        {
            await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error: could not subscribe ({e.Message})");
            return 1;
        }

        var sentAt = new ConcurrentDictionary<long, long>(); // id -> ticks when its publish started
        var latencies = new ConcurrentBag<double>();
        var errors = new ConcurrentBag<string>();
        var clock = Stopwatch.StartNew();
        var tasks = Enumerable.Range(1, publishers).Select(p => Task.Run(async () =>
        {
            int count = messages / publishers + (p <= messages % publishers ? 1 : 0);
            for (int i = 1; i <= count; i++)
            {
                long start = Stopwatch.GetTimestamp();
                try
                {
                    var published = await client.PublishAsync(topic, $"bench-{p}", $"message {i} of publisher {p}", CancellationToken.None);
                    latencies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    if (!sentAt.TryAdd(published.Id, start))
                        errors.Add($"id {published.Id} was given twice");
                }
                catch (BrokerClientException e)
                {
                    errors.Add(e.Message);
                }
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        double publishSeconds = clock.Elapsed.TotalSeconds;

        bool complete = await Task.WhenAny(all.Task, Task.Delay(TimeSpan.FromSeconds(30))) == all.Task && all.Task.IsCompletedSuccessfully;
        double totalSeconds = clock.Elapsed.TotalSeconds;
        await stop.CancelAsync();
        await reader;

        var ids = sentAt.Keys.Order().ToList();
        bool idsContiguous = ids.Count == messages && ids.Count > 0 && ids[0] == firstId && ids[^1] == firstId + messages - 1;
        var received = order; // the reader has finished, so the list does not change any more
        bool inOrder = received.SequenceEqual(ids);
        var delivery = ids.Where(arrived.ContainsKey)
                          .Select(id => Stopwatch.GetElapsedTime(sentAt[id], arrived[id]).TotalMilliseconds).ToList();

        var result = new
        {
            transport = client.GetType().Name.Replace("BrokerClient", "").ToLowerInvariant(),
            messages, publishers,
            published = ids.Count, received = received.Count,
            errors = errors.Count, firstError = errors.FirstOrDefault(),
            idsUniqueAndContiguous = idsContiguous, receivedOnceInOrder = inOrder, complete,
            publishSeconds = Math.Round(publishSeconds, 3), totalSeconds = Math.Round(totalSeconds, 3),
            publishesPerSecond = Math.Round(ids.Count / publishSeconds, 1),
            publishMs = Percentiles(latencies.ToList()),
            deliveryMs = Percentiles(delivery)
        };
        Console.WriteLine($"{ids.Count} of {messages} messages published by {publishers} publishers in {publishSeconds:F2} s " +
                          $"({result.publishesPerSecond} per second), {received.Count} received by the subscriber");
        Console.WriteLine($"ids unique and with no gaps: {(idsContiguous ? "yes" : "NO")}, received once and in order: {(inOrder ? "yes" : "NO")}, errors: {errors.Count}");
        Console.WriteLine($"publish latency ms: {Describe(result.publishMs)}");
        Console.WriteLine($"publish to delivery ms: {Describe(result.deliveryMs)}");
        Console.WriteLine("BENCH " + JsonSerializer.Serialize(result, BrokerRules.Json));
        return idsContiguous && inOrder && errors.IsEmpty ? 0 : 1;
    }

    private static Dictionary<string, double> Percentiles(List<double> values)
    {
        values.Sort();
        double At(double p) => values.Count == 0 ? 0 : Math.Round(values[(int)Math.Min(values.Count - 1, Math.Ceiling(p * values.Count) - 1)], 2);
        return new Dictionary<string, double>
        {
            ["p50"] = At(0.50), ["p90"] = At(0.90), ["p99"] = At(0.99), ["max"] = values.Count == 0 ? 0 : Math.Round(values[^1], 2),
            ["mean"] = values.Count == 0 ? 0 : Math.Round(values.Average(), 2)
        };
    }

    private static string Describe(Dictionary<string, double> p) =>
        $"p50 {p["p50"]}, p90 {p["p90"]}, p99 {p["p99"]}, max {p["max"]}, mean {p["mean"]}";
}
