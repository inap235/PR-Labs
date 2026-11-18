using System.Net;

namespace Labs.Lab3.Chat;

/// A peer that is online. Endpoint is the address and port of its own socket: private messages
/// go there. Immutable: the table replaces the whole entry, so a reader never sees half a change.
public sealed record Peer(string Name, string Id, IPEndPoint Endpoint, DateTime LastHeard);

/// The peers that are online, by name (unique, not case sensitive).
/// The network thread changes it, the keyboard thread reads it (/users, /msg): every access takes the lock.
public sealed class PeerTable
{
    private readonly Dictionary<string, Peer> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public Peer? Find(string name)
    {
        lock (_lock)
            return _byName.GetValueOrDefault(name);
    }

    /// The peer with this name and id was heard from: remember when and from where.
    /// False if it is not in the table (unknown, or its name belongs to another id).
    public bool Touch(string name, string id, IPEndPoint endpoint, DateTime now)
    {
        lock (_lock)
        {
            if (!_byName.TryGetValue(name, out var peer) || peer.Id != id)
                return false;
            _byName[name] = peer with { Endpoint = endpoint, LastHeard = now };
            return true;
        }
    }

    /// Adds the peer, or replaces the one with the same name. Returns the one it replaced.
    public Peer? Put(Peer peer)
    {
        lock (_lock)
        {
            _byName.Remove(peer.Name, out var old); // removed first, so the new spelling of the name is kept
            _byName[peer.Name] = peer;
            return old;
        }
    }

    /// Adds the peer only if its name is free. False when another peer has the name.
    public bool TryAdd(Peer peer)
    {
        lock (_lock)
            return _byName.TryAdd(peer.Name, peer);
    }

    /// Removes the peer only if the name still belongs to this id.
    public bool Remove(string name, string id)
    {
        lock (_lock)
            return _byName.TryGetValue(name, out var peer) && peer.Id == id && _byName.Remove(name);
    }

    /// Removes and returns the peers not heard from since the given time.
    public List<Peer> RemoveSilent(DateTime heardBefore)
    {
        lock (_lock)
        {
            var gone = _byName.Values.Where(p => p.LastHeard < heardBefore).ToList();
            foreach (var peer in gone)
                _byName.Remove(peer.Name);
            return gone;
        }
    }

    public List<Peer> All()
    {
        lock (_lock)
            return _byName.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
