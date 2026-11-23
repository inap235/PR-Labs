using System.Collections.Concurrent;
using System.Text.Json;

namespace Labs.Lab4.Core;

/// The messages of every topic, in memory and on disk (Task 2), one file per topic: data/<topic>.json.
/// Each topic has its own lock, so publishes to different topics do not wait for each other,
/// and two publishes to the same topic never get the same id.
public sealed class MessageStore
{
    private sealed class Topic
    {
        public readonly object Lock = new();
        public readonly List<BrokerMessage> Messages = [];
        public long LastId;
    }

    private readonly ConcurrentDictionary<string, Topic> _topics = new();
    private readonly string _dir;

    private MessageStore(string dir) => _dir = dir;

    public string Directory => _dir;

    /// Reads every topic file of the folder. A .tmp file is a write that did not finish
    /// (the broker was killed in the middle of it): the old file next to it is still whole, so the .tmp is deleted.
    public static MessageStore Open(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        var store = new MessageStore(dir);
        foreach (string tmp in System.IO.Directory.GetFiles(dir, "*.json.tmp"))
            File.Delete(tmp);

        foreach (string file in System.IO.Directory.GetFiles(dir, "*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (BrokerRules.NormalizeTopic(name) != name)
                continue; // not a file of the broker
            List<BrokerMessage> messages;
            try
            {
                using var stream = File.OpenRead(file);
                messages = JsonSerializer.Deserialize<List<BrokerMessage>>(stream, BrokerRules.Json) ?? [];
            }
            catch (JsonException e)
            {
                throw new IOException($"The topic file {file} is not valid JSON ({e.Message}). Move it away to start the broker.", e);
            }
            var topic = new Topic();
            topic.Messages.AddRange(messages.OrderBy(m => m.Id));
            topic.LastId = topic.Messages.Count == 0 ? 0 : topic.Messages[^1].Id;
            store._topics[name] = topic;
        }
        return store;
    }

    /// Gives the message the next id of its topic and the current time, and saves the topic to disk.
    /// accepted (which puts the message in the queue) runs while the topic is still locked, so the queue
    /// gets the messages of a topic in the order of their ids. If the file cannot be saved, the message is
    /// taken back, its id is not used, and the IOException goes to the caller.
    public BrokerMessage Add(string topicName, string sender, string body, Action<BrokerMessage> accepted)
    {
        var topic = _topics.GetOrAdd(topicName, _ => new Topic());
        lock (topic.Lock)
        {
            var message = new BrokerMessage(topic.LastId + 1, topicName, sender, body, DateTimeOffset.UtcNow);
            topic.Messages.Add(message);
            try
            {
                Save(topicName, topic.Messages);
            }
            catch
            {
                topic.Messages.RemoveAt(topic.Messages.Count - 1);
                throw;
            }
            topic.LastId = message.Id;
            accepted(message);
            return message;
        }
    }

    /// Writes the whole topic to <topic>.json.tmp, forces it to the disk, and then puts it in the place of <topic>.json.
    /// The replace is a single rename, so a broker killed at any moment leaves either the old file or the new one, never half of one.
    private void Save(string topicName, List<BrokerMessage> messages)
    {
        string file = Path.Combine(_dir, topicName + ".json");
        string tmp = file + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, messages, BrokerRules.JsonFile);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, file, overwrite: true);
    }

    /// The last id of a topic (0 if it has no messages) and the messages after afterId (none if afterId is null).
    public (long LastId, List<BrokerMessage> After) Read(string topicName, long? afterId)
    {
        if (!_topics.TryGetValue(topicName, out var topic))
            return (0, []);
        lock (topic.Lock)
        {
            var after = afterId is { } id ? topic.Messages.Where(m => m.Id > id).ToList() : [];
            return (topic.LastId, after);
        }
    }

    /// Every topic with its number of messages and its last id.
    public List<(string Name, long Size, long LastId)> Sizes()
    {
        var sizes = new List<(string, long, long)>();
        foreach (var (name, topic) in _topics)
        {
            lock (topic.Lock)
                sizes.Add((name, topic.Messages.Count, topic.LastId));
        }
        return sizes;
    }
}
