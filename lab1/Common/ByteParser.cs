using System.Runtime.CompilerServices;

namespace Labs.Lab1.Common;

/// <summary>
/// Parses ASCII integers, one per line, straight from bytes. It creates no strings, so the work
/// measured is reading and parsing only, not garbage collection.
/// </summary>
public static class ByteParser
{
    /// <summary>Parses every number in <paramref name="text"/>, which must hold whole lines only.</summary>
    public static Counts CountLines(ReadOnlySpan<byte> text)
    {
        long negative = 0, zero = 0, positive = 0;
        int i = 0;
        while (i < text.Length)
        {
            byte c = text[i];
            if (c == '\n' || c == '\r')
            {
                i++;
                continue;
            }

            int value = ParseNumber(text, ref i);
            if (value < 0) negative++;
            else if (value > 0) positive++;
            else zero++;
        }
        return new Counts(negative, zero, positive);
    }

    /// <summary>Parses every number in <paramref name="text"/> into <paramref name="values"/>, growing it if needed.</summary>
    public static void ParseInto(ReadOnlySpan<byte> text, ref int[] values, ref int count)
    {
        int i = 0;
        while (i < text.Length)
        {
            byte c = text[i];
            if (c == '\n' || c == '\r')
            {
                i++;
                continue;
            }

            int value = ParseNumber(text, ref i);
            if (count == values.Length)
                Array.Resize(ref values, Math.Max(16, values.Length * 2));
            values[count++] = value;
        }
    }

    /// <summary>Parses one number starting at <paramref name="i"/> and leaves <paramref name="i"/> at the end of its line.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ParseNumber(ReadOnlySpan<byte> text, ref int i)
    {
        bool negative = false;
        if (text[i] == (byte)'-')
        {
            negative = true;
            i++;
        }

        int start = i;
        int value = 0;
        while (i < text.Length)
        {
            uint digit = (uint)(text[i] - '0');
            if (digit > 9)
                break;
            value = value * 10 + (int)digit;
            i++;
        }

        if (i == start || (i < text.Length && text[i] != '\n' && text[i] != '\r'))
            throw new FormatException($"Invalid number near byte {i} of a chunk.");
        return negative ? -value : value;
    }
}
