using System.Diagnostics;
using System.Globalization;
using Labs.Lab1.Common;

namespace Labs.Lab1.Generation;

/// <summary>Generates numbers.txt: random integers from -1 000 000 to 1 000 000, one per line.</summary>
public static class Generator
{
    private const int Min = -1_000_000;
    private const int Max = 1_000_000;

    public static void Run(Options options)
    {
        long count = options.GetLong("count", 50_000_000, min: 1);
        int seed = options.GetInt("seed", 12345);
        string path = Paths.NumbersFile(options);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var stopwatch = Stopwatch.StartNew();
        var random = new Random(seed); // fixed seed: the same file every time
        var buffer = new byte[LineChunkReader.DefaultBufferSize];
        int used = 0;
        Counts counts = default;

        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 0))
        {
            for (long i = 0; i < count; i++)
            {
                int value = random.Next(Min, Max + 1);

                if (buffer.Length - used < 16)
                {
                    file.Write(buffer, 0, used);
                    used = 0;
                }
                value.TryFormat(buffer.AsSpan(used), out int written, provider: CultureInfo.InvariantCulture);
                used += written;
                buffer[used++] = (byte)'\n';

                if (value < 0) counts.Negative++;
                else if (value > 0) counts.Positive++;
                else counts.Zero++;
            }
            file.Write(buffer, 0, used);
        }

        Reference.Save(options, counts);
        Console.WriteLine($"Generated {path} ({new FileInfo(path).Length / (1024.0 * 1024.0):F1} MB) in {stopwatch.Elapsed.TotalSeconds:F1} s");
        Console.WriteLine($"Reference counts: {counts}");
    }
}
