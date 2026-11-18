using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Labs.Lab3.Chat;

public sealed record ChatOptions(string Name, int Port, string? Passphrase, bool Broadcast, IPAddress? Interface, string LogFile);

/// One peer of the chat. There is no server: every running copy of the program is one of these,
/// and it both sends and receives.
///
/// Two sockets:
///   _shared  bound to 0.0.0.0:<chat port> with ReuseAddress, like every peer of this computer, and
///            joined to the multicast group. Receives the general channel: hello, join, alive, bye, chat.
///            A multicast or broadcast datagram reaches every socket bound to the port; a unicast one
///            only one of them, so nothing for one peer only is ever sent to this port.
///   _own     bound to 0.0.0.0:0, a free port the system chooses, different for every peer. Sends every
///            datagram of this peer and receives what is for this peer only: here, taken, private, ack.
///            The others see it as the source of our datagrams: it is our address in the chat.
///
/// Two threads:
///   main     reads the keyboard, sends the general and the private messages
///   network  waits for both sockets with Select and handles every datagram; at least every 0.2 s it
///            also checks the timers: alive every 5 s, peers silent for 15 s, private messages without ack
public sealed class ChatPeer
{
    private enum State { Joining, Online, Refused, Left }

    /// A datagram that is a valid message, with where it came from.
    private readonly record struct Received(Message Message, IPEndPoint Source, int Size, bool OnShared, bool Known);

    /// A private message waiting for its ack.
    private sealed class Pending(Peer to, byte[] datagram, string text)
    {
        public Peer To { get; } = to;
        public byte[] Datagram { get; } = datagram;
        public string Text { get; } = text;
        public int Attempts { get; set; } = 1;
        public DateTime NextTry { get; set; } = DateTime.UtcNow + Protocol.AckWait;
    }

    private readonly ChatOptions _options;
    private readonly string _name;
    private readonly string _id = Protocol.NewId(); // tells our own datagrams from those of a peer with our name
    private readonly ConsoleUi _ui;
    private readonly DatagramLog _log;
    private readonly LocalNetwork _network;
    private readonly Socket _shared;
    private readonly Socket _own;
    private readonly IPEndPoint _general;  // where the general channel goes: group:port, or broadcast:port
    private readonly IPEndPoint _endpoint; // this peer, as the others see it
    private readonly PrivateCipher? _cipher;

    private readonly PeerTable _peers = new();
    private readonly SeenMessages _seen = new();                 // network thread only
    private readonly Dictionary<long, Pending> _pending = new(); // by seq; both threads: lock on it
    private readonly object _stateLock = new();
    private readonly object _sendLock = new();
    private readonly ManualResetEventSlim _refused = new();

    private State _state = State.Joining;
    private string _refusal = "";
    private long _seq;
    private DateTime _nextAlive;
    private (IPEndPoint To, string Kind, string Name) _lastUnicast; // under _sendLock
    private Thread? _networkThread;
    private volatile bool _stopping;
    private int _left;

    public ChatPeer(ChatOptions options, ConsoleUi ui)
    {
        _options = options;
        _name = options.Name;
        _ui = ui;
        _network = LocalNetwork.Choose(options.Interface);
        _general = new IPEndPoint(options.Broadcast ? _network.Broadcast : Protocol.Group, options.Port);
        var keyTime = Stopwatch.StartNew();
        if (options.Passphrase != null)
            _cipher = PrivateCipher.FromPassphrase(options.Passphrase, options.Port);
        keyTime.Stop();

        _shared = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Several peers of one computer bind the same port. The system allows it only if every one
        // of them asks for it before Bind (SO_REUSEADDR).
        _shared.ExclusiveAddressUse = false;
        _shared.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        // 0.0.0.0: every address of this computer. The datagrams of the general channel are not
        // addressed to our IP but to the group (or the broadcast address); they arrive from the
        // network, never through 127.0.0.1.
        _shared.Bind(new IPEndPoint(IPAddress.Any, options.Port));
        // Join the group on the chosen network (IGMP): from now on the system passes the datagrams
        // of the group to this socket. Broadcasts to the port arrive without joining anything.
        _shared.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
            new MulticastOption(Protocol.Group, _network.Address));

        _own = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _own.Bind(new IPEndPoint(IPAddress.Any, 0)); // port 0: a free port, only this peer's
        // Multicast leaves through the chosen network, not through whatever adapter the system prefers
        _own.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, _network.Address.GetAddressBytes());
        // TTL 1: no router passes the general channel to another network
        _own.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        // Without SO_BROADCAST, sending to a broadcast address fails (used with --broadcast)
        _own.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        _endpoint = new IPEndPoint(_network.Address, ((IPEndPoint)_own.LocalEndPoint!).Port);

        _log = new DatagramLog(options.LogFile);
        _log.Event($"start: {_name}, id {_id}, port {options.Port}, general channel to {_general} through {_network}, " +
                   $"own socket {_endpoint}, private messages {(_cipher != null ? "encrypted" : "not encrypted")}");
        if (_cipher != null)
            _log.Event($"key derived from the passphrase in {keyTime.ElapsedMilliseconds} ms ({PrivateCipher.Iterations} iterations)");
    }

    private State CurrentState
    {
        get
        {
            lock (_stateLock)
                return _state;
        }
    }

    /// Joins, then reads the keyboard until /quit. Returns the exit code of the program.
    public int Run()
    {
        string channel = _options.Broadcast ? $"broadcast to {_network.Broadcast}" : $"multicast group {Protocol.Group}";
        _ui.Print($"LAN chat on port {_options.Port}. General channel: {channel}, through {_network}.");
        if (IPAddress.IsLoopback(_network.Address))
            _ui.Print("Loopback only: only the peers of this computer can be reached (--interface chooses a network).");
        _ui.Print($"This peer: {_endpoint}. Private messages: " +
                  (_cipher != null ? "encrypted (AES-256-GCM, key from the passphrase)." : "not encrypted (no --passphrase)."));
        _ui.Print($"Log: {_log.Path}");

        _networkThread = new Thread(NetworkLoop) { Name = "network", IsBackground = true };
        _networkThread.Start();

        // Task 2: tell everybody that we want to join. The peers that are online answer at once
        // (here), the one that already has our name says so (taken). One second is plenty here.
        _ui.Print($"Joining as {_name}...");
        Send(_general, NewMessage(Kind.Hello));
        _refused.Wait(Protocol.JoinWait);

        _nextAlive = DateTime.UtcNow + Protocol.AliveEvery;
        lock (_stateLock)
        {
            if (_state == State.Joining)
                _state = State.Online;
        }
        if (CurrentState != State.Online)
        {
            _ui.Print($"* {_refusal}. Start again with another --name.");
            Leave();
            return 3;
        }

        Send(_general, NewMessage(Kind.Join));
        var others = _peers.All();
        _ui.Print($"* You joined the chat as {_name}. " +
                  (others.Count == 0 ? "Nobody else is online yet." : $"Online: {string.Join(", ", others.Select(p => p.Name))}."));
        _ui.Print("Type a message for everybody, /msg <name> <text>, /users or /quit.");

        while (CurrentState == State.Online)
        {
            string? line = _ui.ReadLine();
            if (line == null || !HandleLine(line))
                break;
        }
        Leave();
        return 0;
    }

    /// Says goodbye and closes the sockets. Once only: /quit, the end of the input, Ctrl+C or a name conflict.
    public void Leave()
    {
        if (Interlocked.Exchange(ref _left, 1) == 1)
            return;
        bool online;
        lock (_stateLock)
        {
            online = _state == State.Online;
            _state = State.Left;
        }
        if (online)
            Send(_general, NewMessage(Kind.Bye)); // the others remove us at once, not after 15 s

        _stopping = true;
        _shared.Close(); // also wakes the network thread from Select
        _own.Close();
        if (_networkThread != null && Thread.CurrentThread != _networkThread)
            _networkThread.Join(1000);
        _log.Event("left the chat, sockets closed");
        _log.Dispose();
    }

    // ---------------------------------------------------------------- keyboard

    /// Handles one typed line. False means /quit.
    private bool HandleLine(string line)
    {
        // a tab or another control character in a pasted line becomes a space
        line = new string(line.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (line.Length == 0)
            return true;
        if (!line.StartsWith('/'))
        {
            SendChat(line);
            return true;
        }

        string[] words = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (words[0].ToLowerInvariant())
        {
            case "/quit":
                return false;
            case "/users":
                PrintUsers();
                return true;
            case "/msg" when words.Length == 3:
                SendPrivate(words[1], words[2]);
                return true;
            case "/msg":
                _ui.Print("* Usage: /msg <name> <text>");
                return true;
            default:
                _ui.Print($"* Unknown command {words[0]}. Commands: /msg <name> <text>, /users, /quit");
                return true;
        }
    }

    private void SendChat(string text)
    {
        if (!CheckLength(text))
            return;
        Send(_general, NewMessage(Kind.Chat) with { Seq = Interlocked.Increment(ref _seq), Text = text });
        // Shown here, once. The copy that comes back to our shared socket has our id and is not shown.
        _ui.Print($"[general] {_name}: {text}");
    }

    /// Task 3: one datagram, sent only to the receiver's own socket.
    private void SendPrivate(string name, string text)
    {
        if (Protocol.SameName(name, _name))
        {
            _ui.Print("* That is you.");
            return;
        }
        var peer = _peers.Find(name);
        if (peer == null)
        {
            _ui.Print($"* {name} is not online. /users shows who is.");
            return;
        }
        if (!CheckLength(text))
            return;

        var message = NewMessage(Kind.Private) with { Seq = Interlocked.Increment(ref _seq), To = peer.Name };
        message = _cipher != null ? _cipher.Encrypt(message, text) : message with { Text = text };
        byte[] datagram = message.Encode();
        lock (_pending)
            _pending[message.Seq] = new Pending(peer, datagram, text);
        SendBytes(peer.Endpoint, Kind.Private, datagram, "", peer.Name);
        _ui.Print($"[private to {peer.Name}] {_name}: {text}");
    }

    private bool CheckLength(string text)
    {
        if (text.Length <= Protocol.MaxText)
            return true;
        _ui.Print($"* Too long: {text.Length} characters, at most {Protocol.MaxText}. Not sent.");
        return false;
    }

    private void PrintUsers()
    {
        var peers = _peers.All();
        var lines = new List<string>
        {
            $"* {peers.Count + 1} online:",
            $"    {_name + " (you)",-22} {_endpoint}"
        };
        foreach (var peer in peers)
        {
            double seconds = (DateTime.UtcNow - peer.LastHeard).TotalSeconds;
            lines.Add($"    {peer.Name,-22} {peer.Endpoint,-21}  last heard {seconds:0} s ago");
        }
        _ui.Print(string.Join(Environment.NewLine, lines));
    }

    // ---------------------------------------------------------------- network thread

    private void NetworkLoop()
    {
        // The largest UDP datagram fits, so a datagram that is too long is received whole and dropped,
        // instead of being cut to the size of the buffer.
        byte[] buffer = new byte[65536];
        var readable = new List<Socket>(2);
        while (!_stopping)
        {
            try
            {
                readable.Clear();
                readable.Add(_shared);
                readable.Add(_own);
                // Returns when a datagram waits on one of the sockets, or after 0.2 s (in microseconds)
                Socket.Select(readable, null, null, 200_000);
                foreach (var socket in readable)
                    ReceiveOne(socket, buffer);
                CheckTimers(DateTime.UtcNow);
            }
            catch (Exception) when (_stopping)
            {
                return; // the sockets were closed by Leave
            }
            catch (Exception e)
            {
                // whatever one datagram does, the chat goes on
                _log.Event($"error: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    private void ReceiveOne(Socket socket, byte[] buffer)
    {
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        int size;
        try
        {
            size = socket.ReceiveFrom(buffer, ref from);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
        {
            // Windows: a datagram we sent from this socket came back as "ICMP port unreachable"
            OnPortUnreachable();
            return;
        }

        var source = (IPEndPoint)from;
        if (size > Protocol.MaxDatagram)
        {
            _log.Dropped(source, "?", size, $"longer than {Protocol.MaxDatagram} bytes");
            return;
        }
        var message = Message.Decode(buffer, size, out string error);
        if (message == null)
        {
            _log.Dropped(source, "?", size, error);
            return;
        }

        if (message.Id == _id)
        {
            // Task 1: our own general message comes back to our shared socket, like to everybody.
            // It was shown when it was typed; recognised by our id, it is not shown again.
            _log.Received(source, message.Type, size, "own datagram, not shown again");
            return;
        }
        _log.Received(source, message.Type, size);

        // any datagram from a peer we know shows that it is still there
        bool known = _peers.Touch(message.From, message.Id, source, DateTime.UtcNow);
        var r = new Received(message, source, size, socket == _shared, known);
        switch (message.Type)
        {
            case Kind.Hello: OnHello(r); break;
            case Kind.Here: OnHere(r); break;
            case Kind.Taken: OnTaken(r); break;
            case Kind.Join: OnJoin(r); break;
            case Kind.Alive: Learn(r); break;
            case Kind.Bye: OnBye(r); break;
            case Kind.Chat: OnChat(r); break;
            case Kind.Private: OnPrivate(r); break;
            case Kind.Ack: OnAck(r); break;
        }
    }

    private void Drop(Received r, string reason) => _log.Dropped(r.Source, r.Message.Type, r.Size, reason);

    /// A new peer wants to join.
    private void OnHello(Received r)
    {
        var m = r.Message;
        if (Protocol.SameName(m.From, _name))
        {
            lock (_stateLock)
            {
                // Two peers start with the same name at the same moment: each sees the other's hello
                // while it is still joining. The smaller id keeps the name, the other one gives up.
                if (_state == State.Joining && string.CompareOrdinal(m.Id, _id) < 0)
                {
                    RefuseLocked($"Another peer is joining with the name {_name} at this moment");
                    return;
                }
                if (_state is not (State.Joining or State.Online))
                    return;
            }
            Send(r.Source, NewMessage(Kind.Taken), $"the name {_name} is ours");
            return;
        }
        if (CurrentState == State.Online)
            Send(r.Source, NewMessage(Kind.Here)); // our name and id; our endpoint is the source address
    }

    /// An answer to our hello: this peer is online. It is added silently.
    private void OnHere(Received r)
    {
        var m = r.Message;
        if (Protocol.SameName(m.From, _name))
        {
            OnOurName(r);
            return;
        }
        _peers.Put(new Peer(m.From, m.Id, r.Source, DateTime.UtcNow));
    }

    private void OnTaken(Received r)
    {
        if (!Protocol.SameName(r.Message.From, _name))
        {
            Drop(r, $"says that the name {r.Message.From} is taken, but ours is {_name}");
            return;
        }
        OnOurName(r);
    }

    /// The peer has joined: from now on it is online. It replaces an old peer with its name, which
    /// is gone (killed and started again), or the old one would have said "taken".
    private void OnJoin(Received r)
    {
        var m = r.Message;
        if (Protocol.SameName(m.From, _name))
        {
            OnOurName(r);
            return;
        }
        var old = _peers.Put(new Peer(m.From, m.Id, r.Source, DateTime.UtcNow));
        if (old?.Id != m.Id && CurrentState == State.Online)
            _ui.Print($"* {m.From} joined the chat");
    }

    private void OnBye(Received r)
    {
        if (_peers.Remove(r.Message.From, r.Message.Id))
            _ui.Print($"* {r.Message.From} left the chat");
        else
            Drop(r, "this peer is not online");
    }

    private void OnChat(Received r)
    {
        if (!Learn(r))
            return;
        if (!_seen.Add(r.Message.Id, r.Message.Seq))
        {
            Drop(r, "a copy of a message already shown");
            return;
        }
        _ui.Print($"[general] {r.Message.From}: {Printable(r.Message.Text!)}");
    }

    private void OnPrivate(Received r)
    {
        var m = r.Message;
        if (r.OnShared)
        {
            Drop(r, "a private message must come to the peer's own port, not to the shared one");
            return;
        }
        if (!Protocol.SameName(m.To, _name))
        {
            Drop(r, $"for {m.To}, not for {_name}");
            return;
        }
        if (!Learn(r))
            return;
        string? text = PrivateText(m, out string problem);
        if (text == null)
        {
            Drop(r, problem); // no ack: the sender will say that it was not confirmed
            return;
        }

        // An ack for a copy too: the copy came because our first ack was lost.
        Send(r.Source, NewMessage(Kind.Ack) with { Seq = m.Seq });
        if (!_seen.Add(m.Id, m.Seq))
        {
            Drop(r, "sent again because our ack was lost; already shown");
            return;
        }
        _ui.Print($"[private] {m.From}: {Printable(text)}");
    }

    /// The text of a private message, or null with the reason why it cannot be read.
    private string? PrivateText(Message m, out string problem)
    {
        if (_cipher == null)
        {
            problem = "encrypted, but this peer has no --passphrase";
            return m.Text;
        }
        if (m.Data == null)
        {
            problem = "not encrypted, but this chat uses a passphrase";
            return null;
        }
        string? text = _cipher.Decrypt(m);
        problem = "cannot be decrypted: another passphrase, or the datagram was changed on the way";
        if (text is { Length: 0 or > Protocol.MaxText })
        {
            problem = $"text empty or longer than {Protocol.MaxText} characters";
            return null;
        }
        return text;
    }

    private void OnAck(Received r)
    {
        lock (_pending)
        {
            if (_pending.TryGetValue(r.Message.Seq, out var pending) && pending.To.Id == r.Message.Id)
            {
                _pending.Remove(r.Message.Seq);
                return;
            }
        }
        Drop(r, "no private message waits for this ack");
    }

    /// The sender of an alive, chat or private message: true when it is online. A peer we did not
    /// know is added if its name is free: we missed its join (lost, or we were joining ourselves).
    private bool Learn(Received r)
    {
        if (r.Known)
            return true;
        var m = r.Message;
        if (Protocol.SameName(m.From, _name))
        {
            OnOurName(r);
            return false;
        }
        if (_peers.TryAdd(new Peer(m.From, m.Id, r.Source, DateTime.UtcNow)))
        {
            if (CurrentState == State.Online)
                _ui.Print($"* {m.From} joined the chat");
            return true;
        }
        Drop(r, $"the name {m.From} belongs to another peer");
        return false;
    }

    /// Another peer uses our name (here, taken, join, alive, chat or private from it).
    private void OnOurName(Received r)
    {
        lock (_stateLock)
        {
            if (_state == State.Joining)
            {
                RefuseLocked($"The name {_name} is already used in this chat");
                return;
            }
            if (_state != State.Online)
                return;
        }
        // Both of us are online with one name: the answers to a hello were lost, or two networks
        // were joined. The smaller id keeps the name; we tell the other one, so it knows too.
        if (string.CompareOrdinal(r.Message.Id, _id) < 0)
        {
            _ui.Print($"* Another peer is online with the name {_name} too, and keeps it. This one leaves the chat.");
            Leave();
            Environment.Exit(3);
        }
        Send(r.Source, NewMessage(Kind.Taken), $"the name {_name} is ours");
        Drop(r, $"uses our name {_name}");
    }

    private void RefuseLocked(string reason)
    {
        if (_state != State.Joining)
            return;
        _state = State.Refused;
        _refusal = reason;
        _refused.Set();
    }

    private void CheckTimers(DateTime now)
    {
        if (CurrentState == State.Online && now >= _nextAlive)
        {
            Send(_general, NewMessage(Kind.Alive));
            _nextAlive = now + Protocol.AliveEvery;
        }

        // A killed process or a lost network sends nothing: silence is the only sign.
        foreach (var peer in _peers.RemoveSilent(now - Protocol.GoneAfter))
        {
            _log.Event($"{peer.Name} at {peer.Endpoint} not heard from for {Protocol.GoneAfter.TotalSeconds:0} s: gone");
            _ui.Print($"* {peer.Name} left the chat (not heard from for {Protocol.GoneAfter.TotalSeconds:0} s)");
        }

        ResendPrivate(now);
    }

    /// A private message without an ack after 1 s is sent again, the same datagram; after 3 attempts
    /// the sender is told that it was probably not delivered.
    private void ResendPrivate(DateTime now)
    {
        var again = new List<Pending>();
        var failed = new List<Pending>();
        lock (_pending)
        {
            foreach (var (seq, pending) in _pending.ToList())
            {
                if (now < pending.NextTry)
                    continue;
                if (pending.Attempts >= Protocol.SendAttempts)
                {
                    _pending.Remove(seq);
                    failed.Add(pending);
                }
                else
                {
                    pending.Attempts++;
                    pending.NextTry = now + Protocol.AckWait;
                    again.Add(pending);
                }
            }
        }
        foreach (var pending in again)
        {
            SendBytes(pending.To.Endpoint, Kind.Private, pending.Datagram,
                $"no ack yet, attempt {pending.Attempts} of {Protocol.SendAttempts}", pending.To.Name);
        }
        foreach (var pending in failed)
            _ui.Print($"* {pending.To.Name} did not confirm your private message \"{Preview(pending.Text)}\": it was probably not delivered.");
    }

    /// Windows only: the computer of a receiver answered "ICMP port unreachable" to a datagram we
    /// sent: it is there, but no socket is bound to that port any more. The error does not say
    /// which datagram, so it is taken to be the last one sent to one peer.
    private void OnPortUnreachable()
    {
        (IPEndPoint To, string Kind, string Name) last;
        lock (_sendLock)
            last = _lastUnicast;
        _log.Event($"ICMP port unreachable: nobody listens at {last.To} any more, where the last {last.Kind} went");
        if (last.Kind != Kind.Private)
            return;

        int failed;
        lock (_pending)
        {
            var seqs = _pending.Where(p => p.Value.To.Endpoint.Equals(last.To)).Select(p => p.Key).ToList();
            foreach (long seq in seqs)
                _pending.Remove(seq);
            failed = seqs.Count;
        }
        if (failed > 0)
            _ui.Print($"* {last.Name} did not get your private message: nobody listens at {last.To} any more.");
    }

    // ---------------------------------------------------------------- sending

    private Message NewMessage(string kind) => new(kind, _name, _id);

    private void Send(IPEndPoint to, Message message, string note = "") =>
        SendBytes(to, message.Type, message.Encode(), note, "");

    /// Every datagram leaves through the own socket: its source endpoint is our address in the chat.
    private void SendBytes(IPEndPoint to, string kind, byte[] datagram, string note, string toName)
    {
        try
        {
            lock (_sendLock)
            {
                _own.SendTo(datagram, to); // one sendto = one datagram = one message
                if (!to.Equals(_general))
                    _lastUnicast = (to, kind, toName);
            }
            _log.Sent(to, kind, datagram.Length, note);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            string reason = e is SocketException s ? s.SocketErrorCode.ToString() : "socket closed";
            _log.Event($"error: {kind} to {to} not sent ({reason})");
        }
    }

    /// A control character (an escape sequence, a carriage return) from the network could move the
    /// cursor or clear the screen: it is shown as '?'.
    private static string Printable(string text) => new(text.Select(c => char.IsControl(c) ? '?' : c).ToArray());

    private static string Preview(string text) => text.Length <= 30 ? text : text[..30] + "...";

    /// The (id, seq) of the last 1000 messages shown, so that each one is shown only once.
    private sealed class SeenMessages
    {
        private const int Capacity = 1000;
        private readonly HashSet<(string, long)> _set = [];
        private readonly Queue<(string, long)> _order = new();

        public bool Add(string id, long seq)
        {
            if (!_set.Add((id, seq)))
                return false;
            _order.Enqueue((id, seq));
            if (_order.Count > Capacity)
                _set.Remove(_order.Dequeue());
            return true;
        }
    }
}
