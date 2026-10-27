using Labs.Lab1;
using Labs.Lab2;

// Entry point for all labs: the first argument selects the lab, the rest is passed to it.
//   dotnet run -c Release -- lab1 1a --threads 8
//   dotnet run -c Release -- lab2 server --port 5000
if (args.Length == 0)
{
    Console.WriteLine("Usage: Labs <lab> <command> [options]");
    Console.WriteLine("Labs: lab1, lab2");
    return 1;
}

return args[0].ToLowerInvariant() switch
{
    "lab1" => Lab1.Run(args[1..]),
    "lab2" => Lab2.Run(args[1..]),
    _ => Fail($"Unknown lab '{args[0]}'. Labs: lab1, lab2")
};

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
