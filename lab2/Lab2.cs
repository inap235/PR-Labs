using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Labs.Lab2.Server;

namespace Labs.Lab2;

/// Laboratory Work 2: A Coffee Shop Server. The clients are in lab2/client-java.
public static class Lab2
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "server": RunServer(args[1..]); return 0;
                default:
                    Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException or SocketException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 2;
        }
    }

    private static void RunServer(string[] args)
    {
        int port = 5000;
        var bind = IPAddress.Any; // every network interface, so other computers can connect too
        int workers = Environment.ProcessorCount; // one per logical processor
        string stockFile = Path.Combine(Lab2Dir(), "data", "stock.json");

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--port": port = IntValue(args, ref i, 1, 65535); break;
                case "--bind": bind = IPAddress.Parse(Value(args, ref i)); break;
                case "--workers": workers = IntValue(args, ref i, 1, 1000); break;
                case "--stock": stockFile = Path.GetFullPath(Value(args, ref i)); break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(stockFile)!);
        new ShopServer(bind, port, Stock.Load(stockFile), workers).Run();
    }

    /// The lab2 folder, found by walking up from the working directory and the executable,
    /// so it works with dotnet run and from Rider.
    private static string Lab2Dir()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "lab2", "Lab2.cs")))
                    return Path.Combine(dir.FullName, "lab2");
            }
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "lab2");
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
            Usage: Labs lab2 <command> [options]

            Commands:
              server [--port 5000] [--bind 0.0.0.0] [--workers <logical processors>] [--stock lab2/data/stock.json]
                     start the coffee shop server; --bind 127.0.0.1 accepts only this computer,
                     --workers 2 makes it easy to show more clients than workers,
                     --stock is the file that keeps the stock between runs (delete it for a new random stock)
            """);
    }
}
