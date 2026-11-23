using System.Text.Json;

namespace Labs.Lab4.Core;

/// Task 3: the publishers that may publish, read from a JSON file:
///   { "publishers": [ { "name": "teacher", "token": "...", "topics": ["grades", "news"] } ] }
/// A topic "*" allows every topic. The file is read again when it changes, so a publisher can be
/// added or removed while the broker runs.
public sealed class TokenFile
{
    public sealed record Publisher(string Name, string Token, string[] Topics)
    {
        public bool MayPublish(string topic) => Topics.Contains("*") || Topics.Contains(topic);
    }

    private sealed record Content(List<Publisher>? Publishers);

    private readonly object _lock = new();
    private readonly BrokerLog _log;
    private Dictionary<string, Publisher> _byToken = [];
    private DateTime _readAt;

    public TokenFile(string path, BrokerLog log)
    {
        Path = System.IO.Path.GetFullPath(path);
        _log = log;
        if (!File.Exists(Path))
            throw new FileNotFoundException($"The token file {Path} does not exist.");
        _byToken = Read(); // a bad file at the start stops the broker with the reason
        _readAt = File.GetLastWriteTimeUtc(Path);
        _log.Info($"tokens: {_byToken.Count} publishers from {Path}");
    }

    public string Path { get; }

    /// The publisher of the token in "Authorization: Bearer <token>", or null without a token or with an unknown one.
    public Publisher? Identify(string? authorization)
    {
        if (authorization is null)
            return null;
        string[] parts = authorization.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            return null;
        lock (_lock)
        {
            ReloadIfChanged();
            return _byToken.GetValueOrDefault(parts[1]);
        }
    }

    private void ReloadIfChanged()
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(Path);
            if (written == _readAt)
                return;
            _byToken = Read();
            _readAt = written;
            _log.Info($"tokens: the file changed, {_byToken.Count} publishers");
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            // half-saved by an editor, or wrong: keep the tokens that we have, and try again at the next publish
            _log.Info($"tokens: could not read the file again, keeping the old tokens ({e.Message})");
        }
    }

    private Dictionary<string, Publisher> Read()
    {
        Content? content;
        using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            content = JsonSerializer.Deserialize<Content>(stream, BrokerRules.Json);

        var byToken = new Dictionary<string, Publisher>(StringComparer.Ordinal);
        foreach (var p in content?.Publishers ?? [])
        {
            if (string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Token) || p.Topics is null)
                throw new InvalidDataException("every publisher needs a name, a token and a list of topics");
            var topics = p.Topics.Select(t => t == "*" ? t : BrokerRules.NormalizeTopic(t)
                                              ?? throw new InvalidDataException($"'{t}' of {p.Name} is not a valid topic")).ToArray();
            if (!byToken.TryAdd(p.Token, p with { Topics = topics }))
                throw new InvalidDataException($"the token of {p.Name} is used twice");
        }
        return byToken;
    }
}
