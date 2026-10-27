package coffeeshop;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;

/// Cuts the received bytes into lines ending with '\n', like LineFramer on the server.
/// TCP has no message boundaries: one read may bring half a line or several lines.
/// Bytes after the last '\n' wait in the buffer for the next read.
final class LineReader {
    private final InputStream in;
    private final byte[] buffer = new byte[4096];
    private int start; // unread bytes are buffer[start..end)
    private int end;
    private final ByteArrayOutputStream line = new ByteArrayOutputStream();

    LineReader(InputStream in) {
        this.in = in;
    }

    /// The next line, or null when the server closed the connection.
    String readLine() throws IOException {
        while (true) {
            while (start < end) {
                byte b = buffer[start++];
                if (b == '\n') {
                    String text = line.toString(StandardCharsets.UTF_8);
                    line.reset();
                    return text.endsWith("\r") ? text.substring(0, text.length() - 1) : text;
                }
                line.write(b);
            }

            int count = in.read(buffer); // blocks until bytes arrive
            if (count == -1)
                return null; // the server closed its side
            start = 0;
            end = count;
        }
    }
}
