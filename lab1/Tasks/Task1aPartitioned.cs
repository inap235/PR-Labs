using Labs.Lab1.Common;

namespace Labs.Lab1.Tasks;

/// <summary>
/// Task 1a: the file is divided into as many byte ranges as there are threads. Each thread opens
/// the file itself, reads only its own range and parses it into its own counters.
/// </summary>
public static class Task1aPartitioned
{
    public static void Run(Options options)
    {
        int threads = options.GetInt("threads", 1, min: 1);
        int bufferKb = options.GetInt("buffer-kb", 1024, min: 4);
        string path = Paths.RequireFile(Paths.NumbersFile(options));
        long length = new FileInfo(path).Length;

        var measured = Measurement.Measure(() =>
        {
            var results = new Counts[threads];
            ThreadRunner.Run(threads, t =>
            {
                var (from, to) = ThreadRunner.Range(length, t, threads);
                results[t] = CountPart(path, from, to, bufferKb * 1024);
            });
            return Counts.Sum(results);
        });

        Measurement.Report(options, "1a", $"buf{bufferKb}KB", threads, measured);
    }

    /// <summary>
    /// Counts every line whose first byte lies in [from, to). Both ends are moved forward to the
    /// next line start, so this part ends exactly where the next part begins.
    /// </summary>
    private static Counts CountPart(string path, long from, long to, int bufferSize)
    {
        using var file = Paths.OpenRead(path);
        long begin = LineChunkReader.AlignToLineStart(file, from);
        long end = LineChunkReader.AlignToLineStart(file, to);

        Counts counts = default;
        var buffer = new byte[bufferSize];
        LineChunkReader.Read(file, begin, end, buffer, chunk => counts.Add(ByteParser.CountLines(chunk)));
        return counts;
    }
}
