package lanchat;

import java.io.BufferedReader;
import java.io.FileDescriptor;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.PrintStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.net.SocketException;
import java.net.StandardSocketOptions;
import java.nio.charset.StandardCharsets;
import java.nio.file.Path;
import java.security.GeneralSecurityException;
import java.security.SecureRandom;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.HexFormat;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;
import java.util.concurrent.atomic.AtomicBoolean;

/// The chat of lab 3 in Java (bonus): a peer that follows lab3/PROTOCOL.md, so it chats with the
/// C# peers. The same two sockets as the C# peer:
///   shared  0.0.0.0:<chat port>, SO_REUSEADDR, joined to the group: the general channel
///   own     0.0.0.0:<free port>: sends everything, receives here, taken, private and ack
/// Four threads: main reads the keyboard; one receiver for each socket (DatagramSocket has no
/// select); a timer thread for alive, silent peers and acks. Everything they share is guarded by
/// one lock.
public final class LanChat {
    static final String GROUP = "239.255.42.42";
    static final int MAX_DATAGRAM = 4096;
    static final long JOIN_WAIT = 1000, ALIVE_EVERY = 5000, GONE_AFTER = 15000, ACK_WAIT = 1000;
    static final int SEND_ATTEMPTS = 3;

    private enum State { JOINING, ONLINE, REFUSED, LEFT }

    /// A peer that is online; endpoint is its own socket.
    private record Peer(String name, String id, InetSocketAddress endpoint, long lastHeard) {
    }

    /// A private message waiting for its ack.
    private static final class Pending {
        final Peer to;
        final byte[] datagram;
        final String text;
        int attempts = 1;
        long nextTry = now() + ACK_WAIT;

        Pending(Peer to, byte[] datagram, String text) {
            this.to = to;
            this.datagram = datagram;
            this.text = text;
        }
    }

    private final Object lock = new Object();
    private final String name;
    private final String id;
    private final int port;
    private final boolean broadcast;
    private final InterfaceAddress network;
    private final DatagramSocket shared;
    private final DatagramSocket own;
    private final InetSocketAddress general;
    private final InetSocketAddress endpoint;
    private final PrivateCipher cipher; // null without a passphrase
    private final DatagramLog log;

    private final Map<String, Peer> peers = new TreeMap<>(String.CASE_INSENSITIVE_ORDER);
    private final Map<Long, Pending> pending = new HashMap<>();
    private final Set<String> seen = new HashSet<>();
    private final ArrayDeque<String> seenOrder = new ArrayDeque<>();
    private final AtomicBoolean left = new AtomicBoolean();
    private State state = State.JOINING;
    private String refusal = "";
    private long seq;
    private long nextAlive;
    private volatile boolean stopping;

    public static void main(String[] args) {
        String name = null, passphrase = null, wanted = null, logFile = null;
        int port = 5000;
        boolean broadcast = false;
        for (int i = 0; i < args.length; i++) {
            switch (args[i]) {
                case "--name" -> name = value(args, ++i);
                case "--port" -> port = parsePort(value(args, ++i));
                case "--passphrase" -> passphrase = value(args, ++i);
                case "--broadcast" -> broadcast = true;
                case "--interface" -> wanted = value(args, ++i);
                case "--log" -> logFile = value(args, ++i);
                default -> usage("unknown option " + args[i]);
            }
        }
        // On Windows the Java launcher decodes the command line with the ANSI code page (Cp1251, Cp1252):
        // a letter outside it (ă, ș) arrives as '?' and would give another key. The environment is
        // read as Unicode, so a passphrase with such letters can be given there instead.
        if (passphrase == null)
            passphrase = System.getenv("LANCHAT_PASSPHRASE");
        else if (passphrase.contains("?") && !"UTF-8".equalsIgnoreCase(System.getProperty("sun.jnu.encoding")))
            System.err.println("Warning: the passphrase contains '?'. If you typed a letter like ă or ș, it was lost: "
                    + "set the environment variable LANCHAT_PASSPHRASE instead of --passphrase.");
        if (name == null)
            usage("--name is required");
        if (!Message.isValidName(name))
            usage("the name must be 1 to 16 letters, digits, '_' or '-'");
        if (passphrase != null && passphrase.isEmpty())
            usage("--passphrase must not be empty");

        // Messages in any language: the program reads and writes UTF-8 (on Windows, chcp 65001 first)
        System.setOut(new PrintStream(new FileOutputStream(FileDescriptor.out), true, StandardCharsets.UTF_8));
        if (logFile == null)
            logFile = "lab3/logs/" + name + "-java-" + ProcessHandle.current().pid() + ".log";

        LanChat chat;
        try {
            chat = new LanChat(name, port, passphrase, broadcast, wanted, Path.of(logFile));
        } catch (IOException | GeneralSecurityException e) {
            System.err.println("Error: " + e.getMessage());
            System.exit(2);
            return;
        }
        // Ctrl+C leaves like /quit: the others are told at once instead of after 15 s
        Runtime.getRuntime().addShutdownHook(new Thread(chat::leave));
        System.exit(chat.run());
    }

    private LanChat(String name, int port, String passphrase, boolean broadcast, String wanted, Path logFile)
            throws IOException, GeneralSecurityException {
        this.name = name;
        this.port = port;
        this.broadcast = broadcast;
        byte[] random = new byte[8];
        new SecureRandom().nextBytes(random);
        this.id = HexFormat.of().formatHex(random);
        this.network = chooseNetwork(wanted);
        NetworkInterface adapter = NetworkInterface.getByInetAddress(network.getAddress());
        InetAddress broadcastAddress = broadcastOf(network);
        this.general = new InetSocketAddress(broadcast ? broadcastAddress : InetAddress.getByName(GROUP), port);
        long keyStart = System.nanoTime();
        this.cipher = passphrase != null ? new PrivateCipher(passphrase, port) : null;
        long keyMillis = (System.nanoTime() - keyStart) / 1_000_000;

        // like the C# peer: several peers of one computer share the port, so SO_REUSEADDR before bind
        shared = new DatagramSocket(null);
        shared.setReuseAddress(true);
        // macOS and Linux also want SO_REUSEPORT on every socket of the port (.NET sets both by
        // itself); Windows has no such option, and SO_REUSEADDR alone is enough there
        if (shared.supportedOptions().contains(StandardSocketOptions.SO_REUSEPORT))
            shared.setOption(StandardSocketOptions.SO_REUSEPORT, true);
        shared.bind(new InetSocketAddress(port)); // 0.0.0.0:<port>
        shared.joinGroup(new InetSocketAddress(GROUP, 0), adapter);

        own = new DatagramSocket(new InetSocketAddress(0)); // 0.0.0.0:<a free port>
        own.setOption(StandardSocketOptions.IP_MULTICAST_IF, adapter);
        own.setOption(StandardSocketOptions.IP_MULTICAST_TTL, 1);
        own.setBroadcast(true);
        endpoint = new InetSocketAddress(network.getAddress(), own.getLocalPort());

        log = new DatagramLog(logFile);
        log.event("start: " + name + ", id " + id + ", port " + port + ", general channel to "
                + DatagramLog.endpoint(general) + " through " + network.getAddress().getHostAddress()
                + " (" + adapter.getDisplayName() + "), own socket " + DatagramLog.endpoint(endpoint)
                + ", private messages " + (cipher != null ? "encrypted" : "not encrypted") + " (Java peer)");
        if (cipher != null)
            log.event("key derived from the passphrase in " + keyMillis + " ms (" + PrivateCipher.ITERATIONS + " iterations)");
    }

    private int run() {
        String channel = broadcast ? "broadcast to " + general.getAddress().getHostAddress() : "multicast group " + GROUP;
        println("LAN chat (Java peer) on port " + port + ". General channel: " + channel + ", through "
                + network.getAddress().getHostAddress() + ".");
        println("This peer: " + DatagramLog.endpoint(endpoint) + ". Private messages: "
                + (cipher != null ? "encrypted (AES-256-GCM, key from the passphrase)." : "not encrypted (no --passphrase)."));
        println("Log: " + log.path);

        start("receive shared", () -> receiveLoop(shared, true));
        start("receive own", () -> receiveLoop(own, false));
        start("timers", this::timerLoop);

        println("Joining as " + name + "...");
        send(general, message(Message.HELLO), "");
        synchronized (lock) {
            long end = now() + JOIN_WAIT;
            while (state == State.JOINING && now() < end) {
                try {
                    lock.wait(Math.max(1, end - now()));
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                    break;
                }
            }
            nextAlive = now() + ALIVE_EVERY;
            if (state == State.JOINING)
                state = State.ONLINE;
            if (state != State.ONLINE) {
                println("* " + refusal + ". Start again with another --name.");
                leave();
                return 3;
            }
            send(general, message(Message.JOIN), "");
            println("* You joined the chat as " + name + ". " + (peers.isEmpty()
                    ? "Nobody else is online yet." : "Online: " + String.join(", ", names()) + "."));
        }
        println("Type a message for everybody, /msg <name> <text>, /users or /quit.");

        try (BufferedReader keyboard = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8))) {
            String line;
            while ((line = keyboard.readLine()) != null && handleLine(line)) {
                // handleLine did the work
            }
        } catch (IOException e) {
            println("Keyboard error: " + e.getMessage());
        }
        leave();
        return 0;
    }

    /// Says goodbye and closes the sockets, once (the shutdown hook calls it again).
    private void leave() {
        if (left.getAndSet(true))
            return;
        synchronized (lock) {
            if (state == State.ONLINE)
                send(general, message(Message.BYE), "");
            state = State.LEFT;
            stopping = true;
        }
        shared.close();
        own.close();
        log.event("left the chat, sockets closed");
        log.close();
    }

    // ---------------------------------------------------------------- keyboard

    private boolean handleLine(String line) {
        StringBuilder clean = new StringBuilder();
        for (char c : line.toCharArray())
            clean.append(Character.isISOControl(c) ? ' ' : c);
        line = clean.toString().strip();
        if (line.isEmpty())
            return true;
        if (!line.startsWith("/")) {
            sendChat(line);
            return true;
        }
        String[] words = line.split(" +", 3);
        switch (words[0].toLowerCase()) {
            case "/quit" -> {
                return false;
            }
            case "/users" -> printUsers();
            case "/msg" -> {
                if (words.length == 3)
                    sendPrivate(words[1], words[2].strip());
                else
                    println("* Usage: /msg <name> <text>");
            }
            default -> println("* Unknown command " + words[0] + ". Commands: /msg <name> <text>, /users, /quit");
        }
        return true;
    }

    private void sendChat(String text) {
        if (!checkLength(text))
            return;
        synchronized (lock) {
            send(general, message(Message.CHAT).withSeq(++seq).withText(text), "");
        }
        println("[general] " + name + ": " + text);
    }

    private void sendPrivate(String to, String text) {
        if (to.equalsIgnoreCase(name)) {
            println("* That is you.");
            return;
        }
        if (!checkLength(text))
            return;
        synchronized (lock) {
            Peer peer = peers.get(to);
            if (peer == null) {
                println("* " + to + " is not online. /users shows who is.");
                return;
            }
            Message m = message(Message.PRIVATE).withSeq(++seq).withTo(peer.name());
            try {
                m = cipher != null ? cipher.encrypt(m, text) : m.withText(text);
            } catch (GeneralSecurityException e) {
                println("* Cannot encrypt: " + e.getMessage());
                return;
            }
            byte[] datagram = m.encode();
            pending.put(m.seq(), new Pending(peer, datagram, text));
            sendBytes(peer.endpoint(), Message.PRIVATE, datagram, "");
            println("[private to " + peer.name() + "] " + name + ": " + text);
        }
    }

    private boolean checkLength(String text) {
        if (text.length() <= Message.MAX_TEXT)
            return true;
        println("* Too long: " + text.length() + " characters, at most " + Message.MAX_TEXT + ". Not sent.");
        return false;
    }

    private void printUsers() {
        synchronized (lock) {
            StringBuilder out = new StringBuilder("* " + (peers.size() + 1) + " online:\n");
            out.append(String.format("    %-22s %s%n", name + " (you)", DatagramLog.endpoint(endpoint)));
            for (Peer p : peers.values()) {
                out.append(String.format("    %-22s %-21s  last heard %d s ago%n", p.name(),
                        DatagramLog.endpoint(p.endpoint()), Math.round((now() - p.lastHeard()) / 1000.0)));
            }
            System.out.print(out);
        }
    }

    // ---------------------------------------------------------------- network threads

    private void receiveLoop(DatagramSocket socket, boolean onShared) {
        byte[] buffer = new byte[65536]; // the largest datagram fits, so one too long is seen whole and dropped
        while (!stopping) {
            DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
            try {
                socket.receive(packet);
            } catch (IOException e) {
                if (stopping)
                    return;
                log.event("error: receive failed: " + e.getMessage());
                continue;
            }
            synchronized (lock) {
                try {
                    handleDatagram(packet, onShared);
                } catch (RuntimeException e) {
                    log.event("error: " + e); // whatever one datagram does, the chat goes on
                }
            }
        }
    }

    private void timerLoop() {
        while (!stopping) {
            try {
                Thread.sleep(200);
            } catch (InterruptedException e) {
                return;
            }
            synchronized (lock) {
                if (!stopping)
                    checkTimers(now());
            }
        }
    }

    private void handleDatagram(DatagramPacket packet, boolean onShared) {
        var source = (InetSocketAddress) packet.getSocketAddress();
        int size = packet.getLength();
        if (size > MAX_DATAGRAM) {
            log.dropped(source, "?", size, "longer than " + MAX_DATAGRAM + " bytes");
            return;
        }
        Message m;
        try {
            m = Message.decode(packet.getData(), size);
        } catch (Message.Invalid e) {
            log.dropped(source, "?", size, e.getMessage());
            return;
        }
        if (m.id().equals(id)) {
            log.received(source, m.type(), size, "own datagram, not shown again");
            return;
        }
        log.received(source, m.type(), size, "");

        Peer peer = peers.get(m.from());
        boolean known = peer != null && peer.id().equals(m.id());
        if (known)
            peers.put(peer.name(), new Peer(peer.name(), peer.id(), source, now()));

        switch (m.type()) {
            case Message.HELLO -> onHello(m, source);
            case Message.HERE -> {
                if (m.from().equalsIgnoreCase(name))
                    onOurName(m, source, size);
                else
                    putPeer(m, source);
            }
            case Message.TAKEN -> {
                if (m.from().equalsIgnoreCase(name))
                    onOurName(m, source, size);
                else
                    log.dropped(source, m.type(), size, "says that the name " + m.from() + " is taken, but ours is " + name);
            }
            case Message.JOIN -> {
                if (m.from().equalsIgnoreCase(name)) {
                    onOurName(m, source, size);
                } else {
                    Peer old = putPeer(m, source);
                    if ((old == null || !old.id().equals(m.id())) && state == State.ONLINE)
                        println("* " + m.from() + " joined the chat");
                }
            }
            case Message.ALIVE -> learn(m, source, size, known);
            case Message.BYE -> {
                if (known) {
                    peers.remove(m.from());
                    println("* " + m.from() + " left the chat");
                } else {
                    log.dropped(source, m.type(), size, "this peer is not online");
                }
            }
            case Message.CHAT -> {
                if (!learn(m, source, size, known))
                    return;
                if (!firstTime(m))
                    log.dropped(source, m.type(), size, "a copy of a message already shown");
                else
                    println("[general] " + m.from() + ": " + printable(m.text()));
            }
            case Message.PRIVATE -> onPrivate(m, source, size, known, onShared);
            case Message.ACK -> {
                Pending p = pending.get(m.seq());
                if (p != null && p.to.id().equals(m.id()))
                    pending.remove(m.seq());
                else
                    log.dropped(source, m.type(), size, "no private message waits for this ack");
            }
            default -> log.dropped(source, m.type(), size, "unknown type");
        }
    }

    private void onHello(Message m, InetSocketAddress source) {
        if (m.from().equalsIgnoreCase(name)) {
            // two peers start with one name at the same moment: the smaller id keeps it
            if (state == State.JOINING && m.id().compareTo(id) < 0) {
                refuse("Another peer is joining with the name " + name + " at this moment");
                return;
            }
            if (state == State.JOINING || state == State.ONLINE)
                send(source, message(Message.TAKEN), "the name " + name + " is ours");
            return;
        }
        if (state == State.ONLINE)
            send(source, message(Message.HERE), "");
    }

    private void onPrivate(Message m, InetSocketAddress source, int size, boolean known, boolean onShared) {
        if (onShared) {
            log.dropped(source, m.type(), size, "a private message must come to the peer's own port, not to the shared one");
            return;
        }
        if (!m.to().equalsIgnoreCase(name)) {
            log.dropped(source, m.type(), size, "for " + m.to() + ", not for " + name);
            return;
        }
        if (!learn(m, source, size, known))
            return;
        String text;
        String problem;
        if (cipher == null) {
            text = m.text();
            problem = "encrypted, but this peer has no --passphrase";
        } else if (m.data() == null) {
            text = null;
            problem = "not encrypted, but this chat uses a passphrase";
        } else {
            text = cipher.decrypt(m);
            problem = "cannot be decrypted: another passphrase, or the datagram was changed on the way";
            if (text != null && (text.isEmpty() || text.length() > Message.MAX_TEXT)) {
                text = null;
                problem = "text empty or longer than " + Message.MAX_TEXT + " characters";
            }
        }
        if (text == null) {
            log.dropped(source, m.type(), size, problem);
            return;
        }
        send(source, message(Message.ACK).withSeq(m.seq()), ""); // also for a copy: our first ack was lost
        if (!firstTime(m)) {
            log.dropped(source, m.type(), size, "sent again because our ack was lost; already shown");
            return;
        }
        println("[private] " + m.from() + ": " + printable(text));
    }

    /// The sender is online: known, or added now if its name is free (we missed its join).
    private boolean learn(Message m, InetSocketAddress source, int size, boolean known) {
        if (known)
            return true;
        if (m.from().equalsIgnoreCase(name)) {
            onOurName(m, source, size);
            return false;
        }
        if (!peers.containsKey(m.from())) {
            putPeer(m, source);
            if (state == State.ONLINE)
                println("* " + m.from() + " joined the chat");
            return true;
        }
        log.dropped(source, m.type(), size, "the name " + m.from() + " belongs to another peer");
        return false;
    }

    /// Another peer uses our name.
    private void onOurName(Message m, InetSocketAddress source, int size) {
        if (state == State.JOINING) {
            refuse("The name " + name + " is already used in this chat");
            return;
        }
        if (state != State.ONLINE)
            return;
        if (m.id().compareTo(id) < 0) {
            println("* Another peer is online with the name " + name + " too, and keeps it. This one leaves the chat.");
            leave();
            System.exit(3);
        }
        send(source, message(Message.TAKEN), "the name " + name + " is ours");
        log.dropped(source, m.type(), size, "uses our name " + name);
    }

    private void refuse(String reason) {
        if (state != State.JOINING)
            return;
        state = State.REFUSED;
        refusal = reason;
        lock.notifyAll(); // wakes run(), which waits for the answers to hello
    }

    private Peer putPeer(Message m, InetSocketAddress source) {
        Peer old = peers.remove(m.from());
        peers.put(m.from(), new Peer(m.from(), m.id(), source, now()));
        return old;
    }

    private void checkTimers(long now) {
        if (state == State.ONLINE && now >= nextAlive) {
            send(general, message(Message.ALIVE), "");
            nextAlive = now + ALIVE_EVERY;
        }
        for (Iterator<Peer> it = peers.values().iterator(); it.hasNext(); ) {
            Peer p = it.next();
            if (now - p.lastHeard() > GONE_AFTER) {
                it.remove();
                log.event(p.name() + " at " + DatagramLog.endpoint(p.endpoint()) + " not heard from for 15 s: gone");
                println("* " + p.name() + " left the chat (not heard from for 15 s)");
            }
        }
        for (Iterator<Map.Entry<Long, Pending>> it = pending.entrySet().iterator(); it.hasNext(); ) {
            Pending p = it.next().getValue();
            if (now < p.nextTry)
                continue;
            if (p.attempts >= SEND_ATTEMPTS) {
                it.remove();
                String preview = p.text.length() <= 30 ? p.text : p.text.substring(0, 30) + "...";
                println("* " + p.to.name() + " did not confirm your private message \"" + preview + "\": it was probably not delivered.");
            } else {
                p.attempts++;
                p.nextTry = now + ACK_WAIT;
                sendBytes(p.to.endpoint(), Message.PRIVATE, p.datagram, "no ack yet, attempt " + p.attempts + " of " + SEND_ATTEMPTS);
            }
        }
    }

    /// Each (id, seq) is shown only once; the last 1000 are remembered.
    private boolean firstTime(Message m) {
        String key = m.id() + ":" + m.seq();
        if (!seen.add(key))
            return false;
        seenOrder.add(key);
        if (seenOrder.size() > 1000)
            seen.remove(seenOrder.poll());
        return true;
    }

    // ---------------------------------------------------------------- sending

    private Message message(String kind) {
        return Message.of(kind, name, id);
    }

    private void send(InetSocketAddress to, Message m, String note) {
        sendBytes(to, m.type(), m.encode(), note);
    }

    /// Every datagram leaves through the own socket: its source is our address in the chat.
    private void sendBytes(InetSocketAddress to, String kind, byte[] datagram, String note) {
        try {
            own.send(new DatagramPacket(datagram, datagram.length, to));
            log.sent(to, kind, datagram.length, note);
        } catch (IOException e) {
            log.event("error: " + kind + " to " + DatagramLog.endpoint(to) + " not sent (" + e.getMessage() + ")");
        }
    }

    // ---------------------------------------------------------------- helpers

    private List<String> names() {
        return new ArrayList<>(peers.keySet());
    }

    private static void println(String line) {
        System.out.println(line); // PrintStream is synchronized: lines from two threads never mix
    }

    private static String printable(String text) {
        StringBuilder out = new StringBuilder();
        for (char c : text.toCharArray())
            out.append(Character.isISOControl(c) ? '?' : c);
        return out.toString();
    }

    private static long now() {
        return System.nanoTime() / 1_000_000;
    }

    private static void start(String name, Runnable body) {
        Thread thread = new Thread(body, name);
        thread.setDaemon(true);
        thread.start();
    }

    /// The network with the default route, or the one with the given address. Java cannot list
    /// gateways, so it "connects" a UDP socket to an address outside the network: that sends
    /// nothing, it only makes the system choose the route, and with it our local address.
    private static InterfaceAddress chooseNetwork(String wanted) throws IOException {
        InetAddress address = null;
        if (wanted != null) {
            address = InetAddress.getByName(wanted);
        } else {
            try (DatagramSocket probe = new DatagramSocket()) {
                probe.connect(InetAddress.getByName("192.0.2.1"), 9); // TEST-NET-1, never on a local network
                InetAddress local = probe.getLocalAddress();
                if (local instanceof Inet4Address && !local.isAnyLocalAddress())
                    address = local;
            } catch (SocketException e) {
                // no route at all: loopback below
            }
            if (address == null)
                address = InetAddress.getByName("127.0.0.1");
        }
        NetworkInterface adapter = NetworkInterface.getByInetAddress(address);
        if (adapter != null) {
            for (InterfaceAddress a : adapter.getInterfaceAddresses()) {
                if (a.getAddress().equals(address))
                    return a;
            }
        }
        throw new IOException("this computer has no address " + address.getHostAddress());
    }

    /// The address with all host bits set, e.g. 192.168.1.255 for 192.168.1.5/24.
    private static InetAddress broadcastOf(InterfaceAddress network) throws IOException {
        byte[] bytes = network.getAddress().getAddress();
        int prefix = network.getNetworkPrefixLength();
        for (int bit = prefix; bit < 32; bit++)
            bytes[bit / 8] |= (byte) (0x80 >>> (bit % 8));
        return InetAddress.getByAddress(bytes);
    }

    private static String value(String[] args, int i) {
        if (i >= args.length)
            usage(args[i - 1] + " needs a value");
        return args[i];
    }

    private static int parsePort(String text) {
        try {
            int port = Integer.parseInt(text);
            if (port >= 1 && port <= 65535)
                return port;
        } catch (NumberFormatException e) {
            // reported below
        }
        usage("--port must be a number from 1 to 65535");
        return 0;
    }

    private static void usage(String problem) {
        System.err.println("Error: " + problem);
        System.err.println("Usage: java -cp lab3/chat-java/out lanchat.LanChat --name <name> [--port 5000] "
                + "[--passphrase <text>] [--broadcast] [--interface <address>] [--log <file>]");
        System.exit(1);
    }
}
