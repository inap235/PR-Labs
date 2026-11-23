using System.Globalization;

namespace Labs.Lab4.Core;

/// The options of a command: "--name value" pairs and "--flag" switches, checked against the ones the command knows.
public sealed class CommandLine
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public CommandLine(string[] args, string[] options, string[]? flags = null)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            if (flags != null && flags.Contains(name, StringComparer.OrdinalIgnoreCase))
                _flags.Add(name);
            else if (options.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"{name} needs a value.");
                _values[name] = args[++i];
            }
            else
                throw new ArgumentException($"Unknown option '{name}'.");
        }
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public bool Has(string name) => _flags.Contains(name);

    public string Require(string name, string example) =>
        Get(name) ?? throw new ArgumentException($"{name} is required, for example {name} {example}.");

    public long? Long(string name, long min, long max)
    {
        string? text = Get(name);
        if (text is null)
            return null;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value < min || value > max)
            throw new ArgumentException($"{name} must be a number from {min} to {max}, got '{text}'.");
        return value;
    }

    public int Int(string name, int fallback, int min, int max) => (int)(Long(name, min, max) ?? fallback);

    /// The topic of --topic, checked and in lower case.
    public string Topic(string fallback = "")
    {
        string text = Get("--topic") ?? (fallback.Length > 0 ? fallback : Require("--topic", "grades"));
        return BrokerRules.NormalizeTopic(text) ?? throw new ArgumentException($"'{text}' is not a valid topic: {BrokerRules.TopicHelp}.");
    }

    /// The address of the broker from --broker: "http://host:port", "host:port" or just "host".
    public Uri Broker(int defaultPort)
    {
        string text = Get("--broker") ?? $"localhost:{defaultPort}";
        if (!text.Contains("://"))
            text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"--broker must be an address like http://192.168.1.5:{defaultPort}, got '{Get("--broker")}'.");
        if (uri.IsDefaultPort && !text.Contains($":{uri.Port}"))
            uri = new UriBuilder(uri) { Port = defaultPort }.Uri;
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    /// The token of --token, or of the environment variable BROKER_TOKEN (so it is not seen in the list of processes).
    public string? Token() => Get("--token") ?? Environment.GetEnvironmentVariable("BROKER_TOKEN");
}

/// Where the files of lab 4 are: the lab4 folder, found by walking up from the working directory and the executable,
/// so it works with dotnet run, from Rider, and for the gRPC project in lab4/grpc.
public static class Lab4Paths
{
    public static string Root()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "lab4", "Lab4.cs")))
                    return Path.Combine(dir.FullName, "lab4");
            }
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "lab4");
    }
}
