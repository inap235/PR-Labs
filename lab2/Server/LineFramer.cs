using System.Text;

namespace Labs.Lab2.Server;

/// A complete line, or an error for bytes that are not one.
public readonly record struct Received(string? Line, string? Error);

/// Cuts the received bytes into lines ending with '\n'.
/// TCP has no message boundaries: one Receive may bring half a line or several lines.
/// Bytes after the last '\n' wait in the buffer for the next Receive.
public sealed class LineFramer
{
    public const int MaxLineBytes = 1024;

    // Throws on bytes that are not UTF-8, instead of silently turning them into '?'.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] _line = new byte[MaxLineBytes];
    private int _length;
    private bool _skipping; // the line was too long: drop its bytes up to the next '\n'

    public List<Received> Push(byte[] data, int count)
    {
        var received = new List<Received>();
        for (int i = 0; i < count; i++)
        {
            byte b = data[i];
            if (b == (byte)'\n')
            {
                if (!_skipping)
                    received.Add(Decode());
                _skipping = false;
                _length = 0;
            }
            else if (_skipping)
            {
                // still inside the line that was too long
            }
            else if (_length == MaxLineBytes)
            {
                received.Add(new Received(null, "line too long"));
                _skipping = true;
                _length = 0;
            }
            else
            {
                _line[_length++] = b;
            }
        }
        return received;
    }

    private Received Decode()
    {
        int length = _length;
        if (length > 0 && _line[length - 1] == (byte)'\r')
            length--; // "\r\n" from telnet and Windows tools
        try
        {
            return new Received(StrictUtf8.GetString(_line, 0, length), null);
        }
        catch (DecoderFallbackException)
        {
            return new Received(null, "invalid UTF-8");
        }
    }
}
