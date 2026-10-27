package coffeeshop;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;

import jdk.net.ExtendedSocketOptions;

/// The coffee shop client, or the provider with --role provider.
/// Two threads: main reads the keyboard and sends, the receiver thread prints every message
/// from the server as soon as it arrives, even while the user is typing.
public final class ShopClient {
    private static OutputStream out;

    public static void main(String[] args) {
        String host = "127.0.0.1";
        int port = 5000;
        String role = "client";

        for (int i = 0; i < args.length; i += 2) {
            if (i + 1 == args.length)
                usage(args[i] + " needs a value");
            String value = args[i + 1];
            switch (args[i]) {
                case "--host" -> host = value;
                case "--port" -> port = parsePort(value);
                case "--role" -> role = value;
                default -> usage("unknown option " + args[i]);
            }
        }
        if (!role.equals("client") && !role.equals("provider"))
            usage("--role must be client or provider");

        Socket socket = new Socket();
        try {
            socket.connect(new InetSocketAddress(host, port), 5000);
            keepAlive(socket);
            out = socket.getOutputStream();
        } catch (IOException e) {
            System.err.println("Cannot connect to " + host + ":" + port + ": " + e.getMessage());
            System.exit(1);
        }
        System.out.println("Connected to " + host + ":" + port + " as " + role + ". Type 'help' for the commands.");

        Thread receiver = new Thread(() -> receive(socket), "receiver");
        receiver.setDaemon(true);
        receiver.start();

        send("HELLO " + role);
        readKeyboard(role);
    }

    private static void readKeyboard(String role) {
        BufferedReader keyboard = new BufferedReader(new InputStreamReader(System.in));
        try {
            String text;
            while ((text = keyboard.readLine()) != null) {
                text = text.strip();
                if (text.isEmpty())
                    continue;
                if (text.equalsIgnoreCase("help")) {
                    printHelp(role);
                    continue;
                }
                send(text); // as typed: the server checks it, so its errors can be shown too
            }
        } catch (IOException e) {
            System.err.println("Keyboard error: " + e.getMessage());
        }

        // keyboard closed (Ctrl+Z or Ctrl+D): leave politely; the receiver ends the program on BYE
        send("QUIT");
        sleep(3000);
        System.exit(0);
    }

    /// Runs on its own thread until the server is gone.
    private static void receive(Socket socket) {
        try {
            LineReader lines = new LineReader(socket.getInputStream());
            String line;
            while ((line = lines.readLine()) != null)
                show(line);
            System.out.println("The server closed the connection.");
        } catch (IOException e) {
            System.out.println("Connection to the server lost: " + e.getMessage());
        }
        // main is blocked reading the keyboard and cannot be interrupted, so the program ends here
        System.exit(0);
    }

    /// Only main sends, so no lock is needed. write sends all the bytes or throws.
    private static void send(String line) {
        try {
            out.write((line + "\n").getBytes(StandardCharsets.UTF_8));
            out.flush();
        } catch (IOException e) {
            System.out.println("Connection to the server lost: " + e.getMessage());
            System.exit(0);
        }
    }

    /// Prints the message as it came, plus a short explanation for the important ones.
    private static void show(String line) {
        String[] w = line.split(" +");
        String note = switch (w[0]) {
            case "ACCEPTED" -> w.length == 3 ? "order " + w[1] + " accepted, takes " + w[2] + " s once a worker is free" : "";
            case "READY" -> w.length == 2 ? "order " + w[1] + " is ready!" : "";
            case "REFUSED" -> "not enough in stock, left: " + line.substring("REFUSED".length()).strip();
            case "ALERT" -> w.length == 3 ? "low stock: only " + w[2] + " " + w[1] + " left" : "";
            default -> "";
        };
        System.out.println("<< " + line + (note.isEmpty() ? "" : "    (" + note + ")"));

        if (w[0].equals("MENU")) {
            for (int i = 1; i + 2 < w.length; i += 3)
                System.out.printf("     %-10s %2s s   %s in stock%n", w[i], w[i + 1], w[i + 2]);
        }
    }

    private static void printHelp(String role) {
        System.out.println("Commands (sent to the server as typed):");
        System.out.println("  MENU                          products, preparation time, stock");
        if (role.equals("provider"))
            System.out.println("  DELIVER coffee 20 tea 10      add products to the stock");
        else
            System.out.println("  ORDER croissant 2 tea 1       order products");
        System.out.println("  QUIT                          leave");
    }

    /// Notices a server whose network is gone after about 16 s, like the server does.
    private static void keepAlive(Socket socket) throws IOException {
        socket.setKeepAlive(true);
        try {
            socket.setOption(ExtendedSocketOptions.TCP_KEEPIDLE, 10);
            socket.setOption(ExtendedSocketOptions.TCP_KEEPINTERVAL, 2);
            socket.setOption(ExtendedSocketOptions.TCP_KEEPCOUNT, 3);
        } catch (UnsupportedOperationException e) {
            // this system only has the default keepalive times
        }
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
        System.err.println("Usage: java -cp out coffeeshop.ShopClient [--host 127.0.0.1] [--port 5000] [--role client|provider]");
        System.exit(1);
    }

    private static void sleep(long ms) {
        try {
            Thread.sleep(ms);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
    }
}
