using Labs.Lab1.Common;

namespace Labs.Lab1.Generation;

/// <summary>Splits numbers.txt, in order, into equal files part_1.txt .. part_N.txt for Task 2.</summary>
public static class Splitter
{
    public static void Run(Options options)
    {
        int parts = options.GetInt("parts", 8, min: 1);
        string source = Paths.RequireFile(Paths.NumbersFile(options));
        string outDir = options.Get("out", Paths.PartsDir(options));
        Directory.CreateDirectory(outDir);

        long totalLines = CountLines(source);
        long linesPerPart = totalLines / parts; // the last part also takes any remainder

        var buffer = new byte[LineChunkReader.DefaultBufferSize];
        using var input = Paths.OpenRead(source);
        int part = 0;
        long linesInPart = 0;
        FileStream output = Create(outDir, part);

        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            var span = buffer.AsSpan(0, read);
            int segmentStart = 0, position = 0;

            // Walk the lines of this chunk until the current part is full, then start the next file.
            while (part < parts - 1)
            {
                int newLine = span[position..].IndexOf((byte)'\n');
                if (newLine < 0)
                    break;
                position += newLine + 1;

                if (++linesInPart == linesPerPart)
                {
                    output.Write(span[segmentStart..position]);
                    output.Dispose();
                    Report(outDir, part, linesInPart);
                    part++;
                    linesInPart = 0;
                    output = Create(outDir, part);
                    segmentStart = position;
                }
            }
            output.Write(span[segmentStart..]);
        }

        output.Dispose();
        Report(outDir, part, totalLines - linesPerPart * (parts - 1));
    }

    private static FileStream Create(string dir, int part) =>
        new(Path.Combine(dir, $"part_{part + 1}.txt"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

    private static void Report(string dir, int part, long lines)
    {
        var info = new FileInfo(Path.Combine(dir, $"part_{part + 1}.txt"));
        Console.WriteLine($"{info.FullName}: {lines:N0} lines, {info.Length / (1024.0 * 1024.0):F1} MB");
    }

    private static long CountLines(string path)
    {
        var buffer = new byte[LineChunkReader.DefaultBufferSize];
        using var file = Paths.OpenRead(path);
        long lines = 0;
        int read;
        byte last = (byte)'\n';
        while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            lines += buffer.AsSpan(0, read).Count((byte)'\n');
            last = buffer[read - 1];
        }
        return last == '\n' ? lines : lines + 1;
    }
}
