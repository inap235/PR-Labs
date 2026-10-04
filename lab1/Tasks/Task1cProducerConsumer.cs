using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using Labs.Lab1.Common;

namespace Labs.Lab1.Tasks;

/// <summary>
/// Task 1c: one reader thread reads numbers.txt line by line and puts batches of lines into a
/// bounded blocking queue. The workers take batches from the queue and count their numbers.
///   queue full  -> Add blocks the reader
///   queue empty -> the workers block in GetConsumingEnumerable
///   end of file -> the reader calls CompleteAdding; workers finish once the queue is empty
/// </summary>
public static class Task1cProducerConsumer
{
    public static void Run(Options options)
    {
        int workers = options.GetInt("workers", 1, min: 1);
        int batchSize = options.GetInt("batch", 10_000, min: 1);
        int capacity = options.GetInt("capacity", 100, min: 0); // 0 = no size limit
        string path = Paths.RequireFile(Paths.NumbersFile(options));

        var measured = Measurement.Measure(() => Count(path, workers, batchSize, capacity));

        string variant = $"batch{batchSize}-cap{(capacity == 0 ? "unbounded" : capacity.ToString(CultureInfo.InvariantCulture))}";
        Measurement.Report(options, "1c", variant, workers, measured);
    }

    private static Counts Count(string path, int workers, int batchSize, int capacity)
    {
        using var queue = capacity > 0
            ? new BlockingCollection<string[]>(new ConcurrentQueue<string[]>(), capacity)
            : new BlockingCollection<string[]>(new ConcurrentQueue<string[]>());

        Exception? readerFailure = null;
        var reader = new Thread(() =>
        {
            try
            {
                using var input = new StreamReader(path, Encoding.ASCII, false, 1 << 16);
                var batch = new string[batchSize];
                int used = 0;
                string? line;
                while ((line = input.ReadLine()) != null)
                {
                    batch[used++] = line;
                    if (used == batchSize)
                    {
                        queue.Add(batch); // blocks while the queue is full
                        batch = new string[batchSize];
                        used = 0;
                    }
                }
                if (used > 0)
                {
                    Array.Resize(ref batch, used);
                    queue.Add(batch);
                }
            }
            catch (Exception e)
            {
                readerFailure = e;
            }
            finally
            {
                queue.CompleteAdding(); // closes the queue: workers stop once it is empty
            }
        })
        {
            Name = "reader",
            IsBackground = true
        };

        var results = new Counts[workers];
        reader.Start();
        ThreadRunner.Run(workers, w =>
        {
            long negative = 0, zero = 0, positive = 0;
            // Blocks while the queue is empty; ends when it is empty and closed.
            foreach (var batch in queue.GetConsumingEnumerable())
            {
                foreach (var line in batch)
                {
                    if (line.Length == 0)
                        continue;
                    int value = int.Parse(line, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    if (value < 0) negative++;
                    else if (value > 0) positive++;
                    else zero++;
                }
            }
            results[w] = new Counts(negative, zero, positive);
        });
        reader.Join();

        if (readerFailure != null)
            ExceptionDispatchInfo.Throw(readerFailure);
        return Counts.Sum(results);
    }
}
