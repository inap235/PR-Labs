package lanchat;

import java.io.IOException;
import java.io.PrintWriter;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.time.LocalTime;
import java.time.format.DateTimeFormatter;

/// The log file of the peer, in the same format as the C# peer: a line for every datagram that it
/// sends, receives or drops (time, endpoint, kind, size), and "----" lines for other events.
final class DatagramLog implements AutoCloseable {
    private static final DateTimeFormatter TIME = DateTimeFormatter.ofPattern("HH:mm:ss.SSS");

    private final PrintWriter out;
    final Path path;
    private boolean closed;

    DatagramLog(Path path) throws IOException {
        this.path = path.toAbsolutePath();
        Files.createDirectories(this.path.getParent());
        // autoflush: every line is in the file at once, so it can be watched while the chat runs
        out = new PrintWriter(Files.newBufferedWriter(this.path, StandardCharsets.UTF_8,
                StandardOpenOption.CREATE, StandardOpenOption.APPEND), true);
    }

    void sent(InetSocketAddress to, String kind, int size, String note) {
        datagram("sent", to, kind, size, note);
    }

    void received(InetSocketAddress from, String kind, int size, String note) {
        datagram("recv", from, kind, size, note);
    }

    void dropped(InetSocketAddress from, String kind, int size, String reason) {
        datagram("drop", from, kind, size, reason);
    }

    void event(String text) {
        write("----  " + text);
    }

    static String endpoint(InetSocketAddress a) {
        return a.getAddress().getHostAddress() + ":" + a.getPort();
    }

    private void datagram(String what, InetSocketAddress endpoint, String kind, int size, String note) {
        write(String.format("%s  %-21s  %-7s  %5d B%s", what, endpoint(endpoint), kind, size,
                note.isEmpty() ? "" : "  " + note));
    }

    private synchronized void write(String line) {
        if (!closed)
            out.println(LocalTime.now().format(TIME) + "  " + line);
    }

    @Override
    public synchronized void close() {
        closed = true;
        out.close();
    }
}
