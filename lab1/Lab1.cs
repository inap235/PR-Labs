using Labs.Lab1.Common;
using Labs.Lab1.Generation;
using Labs.Lab1.Tasks;

namespace Labs.Lab1;

/// <summary>Laboratory Work 1: Concurrency in Practice. Every task is selected by a command.</summary>
public static class Lab1
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
            var options = Options.Parse(args[1..]);
            switch (args[0].ToLowerInvariant())
            {
                case "info": HardwareInfo.Print(); break;
                case "gen": Generator.Run(options); break;
                case "split": Splitter.Run(options); break;
                case "1a": Task1aPartitioned.Run(options); break;
                case "1b": Task1bThreeWays.Run(options); break;
                case "1c": Task1cProducerConsumer.Run(options); break;
                case "2": Task2UsbFiles.Run(options); break;
                default:
                    Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                    PrintUsage();
                    return 1;
            }
            return 0;
        }
        catch (Exception e) when (e is ArgumentException or IOException or FormatException or InvalidDataException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: Labs lab1 <command> [options]

            Commands:
              info                              print hardware and runtime information
              gen   [--count 50000000] [--seed 12345]
                                                generate data/numbers.txt and data/numbers.ref.txt
              split [--parts 8]                 split numbers.txt into data/parts/part_1..8.txt
              1a    --threads N [--buffer-kb 1024]
                                                Task 1a: each thread reads and parses its own part of the file
              1b    --mode unsync|lock|local|interlocked [--threads <logical processors>]
                                                Task 1b: count an in-memory array in different ways
              1c    --workers N [--batch 10000] [--capacity 100]
                                                Task 1c: one reader thread, N workers, bounded queue (capacity 0 = unbounded)
              2     --dir <folder with part_*.txt> --threads N [--label usb]
                                                Task 2: one file per unit of work, read in one operation

            Common options:
              --data <dir>   folder holding numbers.txt (default: lab1/data)
              --csv <file>   also append the result as a CSV line to this file
            """);
    }
}
