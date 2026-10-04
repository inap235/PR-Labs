using System.Diagnostics;
using System.Globalization;
using Labs.Lab1.Common;

namespace Labs.Lab1.Tasks;

/// <summary>
/// Task 2: a file is the unit of work. A thread reads a whole file in one operation into one byte
/// buffer, then parses it. Files are dealt to threads round-robin (file i -> thread i mod T), so
/// every thread gets the same number of files when T divides the file count.
/// </summary>
public static class Task2UsbFiles
{
    public static void Run(Options options)
    {
        int threads = options.GetInt("threads", 1, min: 1);
        string dir = options.Get("dir", Paths.PartsDir(options));
        string label = options.Get("label", "usb");

        string[] files = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "part_*.txt").OrderBy(PartNumber).ToArray()
            : [];
        if (files.Length == 0)
            throw new FileNotFoundException($"No part_*.txt files in '{dir}'. Run 'lab1 split' and copy data/parts to the drive.");

        var readTicks = new long[threads];
        var parseTicks = new long[threads];

        var measured = Measurement.Measure(() =>
        {
            var results = new Counts[threads];
            ThreadRunner.Run(threads, t =>
            {
                Counts counts = default;
                for (int i = t; i < files.Length; i += threads)
                {
                    long start = Stopwatch.GetTimestamp();
                    byte[] bytes = File.ReadAllBytes(files[i]); // the whole file in one buffer
                    long read = Stopwatch.GetTimestamp();
                    counts.Add(ByteParser.CountLines(bytes));
                    long parsed = Stopwatch.GetTimestamp();

                    readTicks[t] += read - start;
                    parseTicks[t] += parsed - read;
                }
                results[t] = counts;
            });
            return Counts.Sum(results);
        });

        // Summed over all threads: with one thread this splits the running time into I/O and CPU.
        string notes = string.Create(CultureInfo.InvariantCulture,
            $"files={files.Length};read_ms_sum={ToMs(readTicks.Sum()):F0};parse_ms_sum={ToMs(parseTicks.Sum()):F0}");
        Measurement.Report(options, "2", label, threads, measured, notes);
    }

    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static int PartNumber(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return int.TryParse(name.AsSpan(name.IndexOf('_') + 1), out int n) ? n : int.MaxValue;
    }
}
