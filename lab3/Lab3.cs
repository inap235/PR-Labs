using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Labs.Lab3.Chat;

namespace Labs.Lab3;

/// Laboratory Work 3: A Local Network Chat over UDP. The Java peer (bonus) is in lab3/chat-java.
public static class Lab3
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        // Names and messages in any language (ă, ș, д): the console reads and writes UTF-8.
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "chat": return RunChat(args[1..]);
                case "networks": PrintNetworks(); return 0;
                default:
                    Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException or SocketException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 2;
        }
    }

    private static int RunChat(string[] args)
    {
        string? name = null;
        int port = 5000;
        string? passphrase = null;
        bool broadcast = false;
        IPAddress? network = null;
        string? logFile = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--name": name = Value(args, ref i); break;
                case "--port": port = IntValue(args, ref i, 1, 65535); break;
                case "--passphrase": passphrase = Value(args, ref i); break;
                case "--broadcast": broadcast = true; break;
                case "--interface": network = IPAddress.Parse(Value(args, ref i)); break;
                case "--log": logFile = Value(args, ref i); break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }
        if (name == null)
            throw new ArgumentException("--name is required, for example --name Ana.");
        if (!Protocol.IsValidName(name))
            throw new ArgumentException($"The name must be 1 to {Protocol.MaxName} letters, digits, '_' or '-', got '{name}'.");
        if (passphrase is { Length: 0 })
            throw new ArgumentException("--passphrase must not be empty.");
        if (network != null && network.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("--interface must be an IPv4 address of this computer.");

        logFile ??= Path.Combine(Lab3Dir(), "logs", $"{name}-{Environment.ProcessId}.log");
        var peer = new ChatPeer(new ChatOptions(name, port, passphrase, broadcast, network, logFile), new ConsoleUi());

        // Ctrl+C leaves like /quit: the others are told at once instead of after 15 s
        Console.CancelKeyPress += (_, _) => peer.Leave();
        return peer.Run();
    }

    private static void PrintNetworks()
    {
        Console.WriteLine("IPv4 addresses of this computer (--interface), * = chosen by default:");
        var chosen = LocalNetwork.Choose(null);
        foreach (var network in LocalNetwork.All())
        {
            string mark = network.Address.Equals(chosen.Address) ? "*" : " ";
            Console.WriteLine($"  {mark} {network.Address,-16} mask {network.Mask,-16} broadcast {network.Broadcast,-16} " +
                              $"{(network.HasGateway ? "gateway " : "        ")} {network.Adapter}");
        }
    }

    /// The lab3 folder, found by walking up from the working directory and the executable,
    /// so it works with dotnet run and from Rider.
    private static string Lab3Dir()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "lab3", "Lab3.cs")))
                    return Path.Combine(dir.FullName, "lab3");
            }
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "lab3");
    }

    private static string Value(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"{args[i]} needs a value.");
        return args[++i];
    }

    private static int IntValue(string[] args, ref int i, int min, int max)
    {
        string name = args[i];
        string text = Value(args, ref i);
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
            throw new ArgumentException($"{name} must be a number from {min} to {max}, got '{text}'.");
        return value;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: Labs lab3 <command> [options]

            Commands:
              chat --name <name> [--port 5000] [--passphrase <text>] [--broadcast] [--interface <address>] [--log <file>]
                     join the chat on the local network; every peer of one chat uses the same port
                     (and the same passphrase). --passphrase encrypts the private messages (Task 4),
                     --broadcast sends the general channel as a broadcast instead of to the multicast group,
                     --interface chooses the network by an IPv4 address of this computer,
                     --log is the log file (default lab3/logs/<name>-<process id>.log)
              networks
                     list the IPv4 addresses of this computer, for --interface

            In the chat: text (to everybody), /msg <name> <text> (private), /users, /quit
            """);
    }
}
