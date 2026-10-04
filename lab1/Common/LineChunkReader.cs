namespace Labs.Lab1.Common;

public delegate void ChunkHandler(ReadOnlySpan<byte> wholeLines);

/// <summary>Reads a byte range of a text file in chunks that always end at a line boundary.</summary>
public static class LineChunkReader
{
    public const int DefaultBufferSize = 1 << 20;

    /// <summary>
    /// Moves <paramref name="offset"/> forward to the first byte of a line: the offset itself if a
    /// line starts there (the byte before it is '\n'), otherwise the byte after the next '\n'.
    /// Two neighbouring parts call this for the same offset and get the same answer, so the
    /// aligned parts are contiguous: no line is skipped and none is counted twice.
    /// </summary>
    public static long AlignToLineStart(FileStream file, long offset)
    {
        long length = file.Length;
        if (offset <= 0) return 0;
        if (offset >= length) return length;

        file.Position = offset - 1;
        Span<byte> probe = stackalloc byte[64];
        while (true)
        {
            int read = file.Read(probe);
            if (read == 0)
                return length;
            int newLine = probe[..read].IndexOf((byte)'\n');
            if (newLine >= 0)
                return file.Position - read + newLine + 1;
        }
    }

    /// <summary>
    /// Reads bytes [begin, end) through <paramref name="buffer"/> and passes them to
    /// <paramref name="handler"/>. A line cut at the end of the buffer is moved to the front
    /// and completed by the next read, so a number is never split between two chunks.
    /// </summary>
    public static void Read(FileStream file, long begin, long end, byte[] buffer, ChunkHandler handler)
    {
        file.Position = begin;
        long remaining = end - begin;
        int carry = 0;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length - carry, remaining);
            int read = file.Read(buffer, carry, toRead);
            if (read == 0)
                throw new EndOfStreamException("The file ended before the expected position.");
            remaining -= read;
            int filled = carry + read;

            if (remaining == 0)
            {
                handler(buffer.AsSpan(0, filled));
                return;
            }

            int lastNewLine = buffer.AsSpan(0, filled).LastIndexOf((byte)'\n');
            if (lastNewLine < 0)
                throw new InvalidDataException("A line is longer than the read buffer.");

            handler(buffer.AsSpan(0, lastNewLine + 1));
            carry = filled - (lastNewLine + 1);
            buffer.AsSpan(lastNewLine + 1, carry).CopyTo(buffer);
        }
    }
}
