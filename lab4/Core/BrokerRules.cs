using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Labs.Lab4.Core;

/// The limits and checks that are the same in Part 1 (HTTP) and Part 2 (gRPC).
public static partial class BrokerRules
{
    public const int MaxTopic = 64;
    public const int MaxSender = 32;
    public const int MaxBody = 4000;

    /// The largest request body the HTTP broker reads (a message of MaxBody letters, as UTF-8 JSON, fits easily).
    public const int MaxRequestBytes = 64 * 1024;

    /// Messages waiting for one subscriber; a subscriber that falls this far behind is dropped.
    public const int OutboxSize = 1000;

    /// The HTTP stream sends an empty line after this much silence, so a dead subscriber is noticed
    /// by the broker, and a dead broker by the subscriber (after three heartbeats are missing).
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    /// Lower case, so "Grades" and "grades" are one topic; only these letters, so the name is also a safe file name.
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex TopicPattern();

    /// The topic name in lower case, or null if it is not a valid name.
    public static string? NormalizeTopic(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxTopic)
            return null;
        string lower = name.ToLowerInvariant();
        return TopicPattern().IsMatch(lower) ? lower : null;
    }

    public const string TopicHelp = "a topic is 1 to 64 letters a-z, digits, '-' or '_', starting with a letter or digit";

    /// Why a message cannot be published, or null if it can.
    public static string? CheckMessage(string? sender, string? body)
    {
        if (string.IsNullOrWhiteSpace(sender))
            return "the field 'sender' is missing or empty";
        if (sender.Length > MaxSender)
            return $"the sender has more than {MaxSender} characters";
        if (sender.Any(char.IsControl))
            return "the sender contains control characters";
        if (string.IsNullOrWhiteSpace(body))
            return "the field 'body' is missing or empty";
        if (body.Length > MaxBody)
            return $"the body has more than {MaxBody} characters";
        return null;
    }

    /// JSON of the HTTP API and of the files: camelCase names (id, topic, sender, body, time),
    /// and letters like ă or ș written as they are instead of ă.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// The same, one field per line, for the files on disk (easy to read at the defense).
    public static readonly JsonSerializerOptions JsonFile = new(Json) { WriteIndented = true };
}
