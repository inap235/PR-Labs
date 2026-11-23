using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Labs.Lab4.Core;
using Labs.Lab4.Http;

namespace Labs.Lab4;

/// Laboratory Work 4: A Message Broker. Part 1 (HTTP) is here; Part 2 (gRPC) is the project in lab4/grpc,
/// which shares the folder lab4/Core with this one.
public static class Lab4
{
    public const int DefaultPort = 8080;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        // messages in any language (ă, ș, д): the console reads and writes UTF-8
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            string[] rest = args[1..];
            switch (args[0].ToLowerInvariant())
            {
                case "broker":
                    return RunBroker(rest);
                case "publish":
                {
                    var o = new CommandLine(rest, ["--broker", "--topic", "--name", "--token"]);
                    using var client = new HttpBrokerClient(o.Broker(DefaultPort), o.Token());
                    return ClientPrograms.PublishAsync(client, o.Topic(), Name(o)).GetAwaiter().GetResult();
                }
                case "subscribe":
                {
                    var o = new CommandLine(rest, ["--broker", "--topic", "--after"], ["--ids"]);
                    using var client = new HttpBrokerClient(o.Broker(DefaultPort), null);
                    using var stop = StopOnCtrlC();
                    return ClientPrograms.SubscribeAsync(client, o.Topic(), o.Long("--after", 0, long.MaxValue), o.Has("--ids"), stop.Token)
                                         .GetAwaiter().GetResult();
                }
                case "topics":
                {
                    var o = new CommandLine(rest, ["--broker"]);
                    using var client = new HttpBrokerClient(o.Broker(DefaultPort), null);
                    return ClientPrograms.TopicsAsync(client).GetAwaiter().GetResult();
                }
                case "bench":
                {
                    var o = new CommandLine(rest, ["--broker", "--topic", "--messages", "--publishers", "--token"]);
                    using var client = new HttpBrokerClient(o.Broker(DefaultPort), o.Token());
                    return ClientPrograms.BenchAsync(client, o.Topic("bench"), o.Int("--messages", 1000, 1, 1_000_000),
                                                     o.Int("--publishers", 4, 1, 256)).GetAwaiter().GetResult();
                }
                default:
                    Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException or IOException or UnauthorizedAccessException
                                      or HttpListenerException or SocketException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 2;
        }
    }

    private static int RunBroker(string[] args)
    {
        var o = new CommandLine(args, ["--port", "--data", "--tokens", "--log"]);
        int port = o.Int("--port", DefaultPort, 1, 65535);
        string root = Lab4Paths.Root();
        string data = Path.GetFullPath(o.Get("--data") ?? Path.Combine(root, "data", "http"));

        using var log = new BrokerLog(o.Get("--log") ?? Path.Combine(root, "logs", "broker-http.log"));
        var store = MessageStore.Open(data);
        var tokens = o.Get("--tokens") is { } file ? new TokenFile(file, log) : null;
        var broker = new MessageBroker(store, tokens, log);

        log.Info($"HTTP broker started, messages in {data}, log in {log.Path}");
        foreach (var t in broker.Topics())
            log.Info($"loaded topic '{t.Name}': {t.Size} messages, the next id is {t.LastId + 1}");
        if (tokens is null)
            log.Info("no --tokens: everybody may publish (Tasks 1 and 2)");

        using var stop = StopOnCtrlC();
        new HttpBroker(broker, port).RunAsync(stop.Token).GetAwaiter().GetResult();
        log.Info("HTTP broker stopped");
        return 0;
    }

    private static string Name(CommandLine o)
    {
        string name = o.Require("--name", "teacher").Trim();
        if (BrokerRules.CheckMessage(name, "x") is { } problem)
            throw new ArgumentException($"--name: {problem.Replace("the sender", "the name")}.");
        return name;
    }

    /// Ctrl+C stops the program cleanly instead of killing it.
    private static CancellationTokenSource StopOnCtrlC()
    {
        var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        return stop;
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            Usage: Labs lab4 <command> [options]          (Part 1: the broker over HTTP)

            Commands:
              broker [--port {DefaultPort}] [--data <folder>] [--tokens <file>] [--log <file>]
                     the broker; --data is where the topics are kept (default lab4/data/http),
                     --tokens turns on Task 3 (for example --tokens lab4/tokens.json),
                     --log is the log file (default lab4/logs/broker-http.log)
              publish --topic <topic> --name <name> [--broker http://localhost:{DefaultPort}] [--token <token>]
                     publishes every line you type (the token can also be in the variable BROKER_TOKEN)
              subscribe --topic <topic> [--broker http://localhost:{DefaultPort}] [--after <id>] [--ids]
                     shows the messages of the topic as they arrive; --after 0 first shows the stored ones,
                     --ids also shows the id and the time of every message
              topics [--broker http://localhost:{DefaultPort}]
                     the topics, with their number of messages and of subscribers
              bench [--broker ...] [--topic bench] [--messages 1000] [--publishers 4] [--token <token>]
                     publishes from several publishers at the same time and checks the ids and the delivery

            Part 2 (gRPC): dotnet run --project lab4/grpc -c Release -- help
            """);
    }
}
