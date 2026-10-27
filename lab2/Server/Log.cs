namespace Labs.Lab2.Server;

/// One line per event: time, client endpoint (or "server"), what happened.
/// Console.WriteLine is thread safe, so lines never mix.
public static class Log
{
    public static void Write(string endpoint, string message) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {endpoint,-21}  {message}");
}
