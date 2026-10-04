using System.Diagnostics;
using System.Globalization;

namespace Labs.Lab1.Common;

public readonly record struct Measured(
    Counts Counts,
    double ElapsedMs,
    double BaseWorkingSetMb,
    double PeakWorkingSetMb,
    double PeakPrivateMb);

/// <summary>Measures the running time and the peak memory of one task run.</summary>
public static class Measurement
{
    /// <summary>
    /// Runs <paramref name="work"/> and measures it. The peak values are the high-water marks of
    /// the whole process, which is why every configuration is run in a fresh process.
    /// </summary>
    public static Measured Measure(Func<Counts> work)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        double baseWorkingSet = ToMb(process.WorkingSet64);

        var stopwatch = Stopwatch.StartNew();
        Counts counts = work();
        stopwatch.Stop();

        process.Refresh();
        return new Measured(
            counts,
            stopwatch.Elapsed.TotalMilliseconds,
            baseWorkingSet,
            ToMb(process.PeakWorkingSet64),       // most physical RAM the process ever used
            ToMb(process.PeakPagedMemorySize64)); // most private memory it ever committed

        static double ToMb(long bytes) => bytes / (1024.0 * 1024.0);
    }

    private const string CsvHeader =
        "timestamp,task,variant,threads,time_ms,base_ws_mb,peak_ws_mb,peak_private_mb,negative,zero,positive,total,check,notes";

    /// <summary>Prints the result and, with <c>--csv</c>, appends it to a CSV file.</summary>
    public static void Report(Options options, string task, string variant, int threads, Measured m, string notes = "")
    {
        string check = Check(options, m.Counts);

        Console.WriteLine(
            $"[{task}] {variant} threads={threads}  time={m.ElapsedMs:F1} ms  " +
            $"peak WS={m.PeakWorkingSetMb:F1} MB  peak private={m.PeakPrivateMb:F1} MB  (base WS={m.BaseWorkingSetMb:F1} MB)");
        Console.WriteLine($"      {m.Counts}  -> {check}{(notes.Length > 0 ? "  " + notes : "")}");

        string? csv = options.Get("csv");
        if (csv == null)
            return;

        var inv = CultureInfo.InvariantCulture;
        string line = string.Join(',',
            DateTime.Now.ToString("s", inv), task, variant, threads.ToString(inv),
            m.ElapsedMs.ToString("F2", inv), m.BaseWorkingSetMb.ToString("F2", inv),
            m.PeakWorkingSetMb.ToString("F2", inv), m.PeakPrivateMb.ToString("F2", inv),
            m.Counts.Negative.ToString(inv), m.Counts.Zero.ToString(inv), m.Counts.Positive.ToString(inv),
            m.Counts.Total.ToString(inv), check, notes.Replace(',', ';'));

        string? dir = Path.GetDirectoryName(Path.GetFullPath(csv));
        if (dir != null)
            Directory.CreateDirectory(dir);
        bool newFile = !File.Exists(csv);
        File.AppendAllText(csv, (newFile ? CsvHeader + Environment.NewLine : "") + line + Environment.NewLine);
    }

    /// <summary>OK when the counts equal the reference counts of numbers.txt, WRONG otherwise.</summary>
    private static string Check(Options options, Counts counts)
    {
        Counts? reference = Reference.TryLoad(options);
        if (reference == null)
            return counts.Total == 50_000_000 ? "TOTAL_OK_NO_REF" : "WRONG_TOTAL_NO_REF";
        return counts.SameAs(reference.Value) ? "OK" : "WRONG";
    }
}
