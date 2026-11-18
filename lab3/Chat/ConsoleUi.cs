using System.Text;

namespace Labs.Lab3.Chat;

/// The console of the chat. Messages from the network are printed while the user is typing:
/// the line being typed is erased, the message is printed, and the typed line is drawn again
/// below it. The typing is never interrupted and never mixed with a message.
/// When the input or the output is redirected (a pipe, a file, a test), plain ReadLine and
/// WriteLine are used instead.
public sealed class ConsoleUi
{
    private const string Prompt = "> ";

    private readonly object _lock = new(); // the keyboard thread and the network thread both write
    private readonly StringBuilder _typed = new();
    private readonly bool _interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
    private bool _reading; // the prompt and the typed line are on the screen

    public void Print(string line)
    {
        lock (_lock)
        {
            EraseTyped();
            Console.WriteLine(line);
            DrawTyped();
        }
    }

    /// A line typed by the user, or null when the input ends (Ctrl+Z or Ctrl+D on an empty line,
    /// or the end of a redirected input).
    public string? ReadLine()
    {
        if (!_interactive)
            return Console.ReadLine();

        lock (_lock)
        {
            _reading = true;
            DrawTyped();
        }
        while (true)
        {
            // intercept: the console does not show the key, this class draws it
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            lock (_lock)
            {
                if (key.Key == ConsoleKey.Enter || (key.KeyChar is '\u0004' or '\u001a' && _typed.Length == 0))
                {
                    // the line is not left on the screen: the chat prints it as "[general] name: text"
                    string? line = key.Key == ConsoleKey.Enter ? _typed.ToString() : null;
                    EraseTyped();
                    _typed.Clear();
                    _reading = false;
                    return line;
                }
                if (key.Key == ConsoleKey.Backspace && _typed.Length > 0)
                {
                    EraseTyped();
                    _typed.Length--;
                    DrawTyped();
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    EraseTyped();
                    _typed.Clear();
                    DrawTyped();
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    _typed.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                }
            }
        }
    }

    /// Clears the prompt and the typed line, which may take several rows, and leaves the cursor
    /// where they started. The cursor is at the end of the typed line.
    private void EraseTyped()
    {
        if (!_interactive || !_reading)
            return;
        int length = Prompt.Length + _typed.Length;
        int top = Math.Max(0, Console.CursorTop - (length - Console.CursorLeft) / Console.BufferWidth);
        Console.SetCursorPosition(0, top);
        Console.Write(new string(' ', length));
        Console.SetCursorPosition(0, top);
    }

    private void DrawTyped()
    {
        if (_interactive && _reading)
            Console.Write(Prompt + _typed);
    }
}
