using System.Net;
using System.Text;

namespace Labs.Lab3.Chat;

/// The log file of one peer: a line for every datagram that it sends, receives or drops, with
/// the time, the endpoint, the kind of message and the size, for example
///   00:23:21.528  sent  239.255.42.42:5000     chat       104 B
///   00:23:21.529  recv  192.168.100.15:60685   chat       104 B  own datagram, not shown again
///   00:23:21.556  drop  192.168.100.15:63792   ?           24 B  not JSON
/// and a line starting with "----" for other events (start, peers that join or leave, errors).
/// Both threads write to it, so every line is written under a lock.
public sealed class DatagramLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private bool _closed;

    public DatagramLog(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        // AutoFlush: every line is in the file at once, so it can be watched while the chat runs
        _writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false)) { AutoFlush = true };
    }

    public string Path { get; }

    public void Sent(EndPoint to, string kind, int size, string note = "") => Datagram("sent", to, kind, size, note);

    public void Received(EndPoint from, string kind, int size, string note = "") => Datagram("recv", from, kind, size, note);

    public void Dropped(EndPoint from, string kind, int size, string reason) => Datagram("drop", from, kind, size, reason);

    public void Event(string text) => Write($"----  {text}");

    private void Datagram(string what, EndPoint endpoint, string kind, int size, string note) =>
        Write($"{what}  {endpoint,-21}  {kind,-7}  {size,5} B{(note.Length > 0 ? "  " + note : "")}");

    private void Write(string line)
    {
        lock (_lock)
        {
            if (!_closed)
                _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {line}");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_closed)
                return;
            _closed = true;
            _writer.Dispose();
        }
    }
}
