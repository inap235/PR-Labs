namespace Labs.Lab4.Core;

/// The log of the broker: one line for every request and every delivery, on the console and in a file.
/// Columns: time, client, what happened, topic, id of the message, result.
/// The lock keeps the lines of different threads from mixing.
public sealed class BrokerLog : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter _file;

    public BrokerLog(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _file = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public string Path { get; }

    public void Write(string client, string what, string? topic, long? id, string result) =>
        WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {client,-21}  {what,-34}  " +
                  $"topic={topic ?? "-",-10}  id={(id?.ToString() ?? "-"),-5}  {result}");

    /// A line about the broker itself (start, stop, tokens reloaded).
    public void Info(string text) => WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {"broker",-21}  {text}");

    private void WriteLine(string line)
    {
        lock (_lock)
        {
            Console.WriteLine(line);
            try
            {
                _file.WriteLine(line);
            }
            catch (IOException)
            {
                // a full disk must not stop the broker: the line is still on the console
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
            _file.Dispose();
    }
}
