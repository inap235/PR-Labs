using Labs.Lab1.Common;

namespace Labs.Lab1.Tasks;

/// <summary>
/// Task 1b: all numbers are loaded into an int array first (not measured), then counted by
/// as many threads as there are logical processors, in one of these ways:
///   unsync      - shared counters, no synchronisation (wrong: lost updates)
///   lock        - shared counters, every increment inside a lock (right, but slow)
///   local       - each thread counts in its own local counters, summed after Join (right and fast)
///   interlocked - shared counters with atomic increments (extra, not required by the lab)
/// </summary>
public static class Task1bThreeWays
{
    /// <summary>Counters shared by all threads. The three fields share one 64-byte cache line.</summary>
    private sealed class SharedCounters
    {
        public long Negative;
        public long Zero;
        public long Positive;

        public Counts ToCounts() => new(Negative, Zero, Positive);
    }

    public static void Run(Options options)
    {
        string mode = options.Get("mode", "local").ToLowerInvariant();
        int threads = options.GetInt("threads", Environment.ProcessorCount, min: 1);

        Func<int[], int, Counts> count = mode switch
        {
            "unsync" => CountUnsynchronized,
            "lock" => CountWithLock,
            "local" => CountLocal,
            "interlocked" => CountInterlocked,
            _ => throw new ArgumentException($"--mode must be unsync, lock, local or interlocked, got '{mode}'.")
        };

        Console.WriteLine("Loading numbers into memory (not measured)...");
        int[] numbers = Load(options);

        var measured = Measurement.Measure(() => count(numbers, threads));
        Measurement.Report(options, "1b", mode, threads, measured);
    }

    private static Counts CountUnsynchronized(int[] numbers, int threads)
    {
        var shared = new SharedCounters();
        ThreadRunner.Run(threads, t =>
        {
            var (from, to) = ThreadRunner.Range(numbers.Length, t, threads);
            for (long i = from; i < to; i++)
            {
                int value = numbers[i];
                // Read-modify-write on shared memory: load, add 1, store. Not atomic.
                if (value < 0) shared.Negative++;
                else if (value > 0) shared.Positive++;
                else shared.Zero++;
            }
        });
        return shared.ToCounts();
    }

    private static Counts CountWithLock(int[] numbers, int threads)
    {
        var shared = new SharedCounters();
        var gate = new Lock();
        ThreadRunner.Run(threads, t =>
        {
            var (from, to) = ThreadRunner.Range(numbers.Length, t, threads);
            for (long i = from; i < to; i++)
            {
                int value = numbers[i];
                lock (gate)
                {
                    if (value < 0) shared.Negative++;
                    else if (value > 0) shared.Positive++;
                    else shared.Zero++;
                }
            }
        });
        return shared.ToCounts();
    }

    private static Counts CountLocal(int[] numbers, int threads)
    {
        var results = new Counts[threads];
        ThreadRunner.Run(threads, t =>
        {
            var (from, to) = ThreadRunner.Range(numbers.Length, t, threads);
            long negative = 0, zero = 0, positive = 0; // private to this thread, live in registers
            for (long i = from; i < to; i++)
            {
                int value = numbers[i];
                if (value < 0) negative++;
                else if (value > 0) positive++;
                else zero++;
            }
            results[t] = new Counts(negative, zero, positive); // one write per thread, at the end
        });
        return Counts.Sum(results); // all threads have been joined, so this read is safe
    }

    private static Counts CountInterlocked(int[] numbers, int threads)
    {
        var shared = new SharedCounters();
        ThreadRunner.Run(threads, t =>
        {
            var (from, to) = ThreadRunner.Range(numbers.Length, t, threads);
            for (long i = from; i < to; i++)
            {
                int value = numbers[i];
                if (value < 0) Interlocked.Increment(ref shared.Negative);
                else if (value > 0) Interlocked.Increment(ref shared.Positive);
                else Interlocked.Increment(ref shared.Zero);
            }
        });
        return shared.ToCounts();
    }

    private static int[] Load(Options options)
    {
        string path = Paths.RequireFile(Paths.NumbersFile(options));
        int capacity = (int)(Reference.TryLoad(options)?.Total ?? 1 << 20);
        var values = new int[capacity];
        int count = 0;

        using var file = Paths.OpenRead(path);
        var buffer = new byte[LineChunkReader.DefaultBufferSize];
        LineChunkReader.Read(file, 0, file.Length, buffer, chunk => ByteParser.ParseInto(chunk, ref values, ref count));

        if (count != values.Length)
            Array.Resize(ref values, count);
        return values;
    }
}
