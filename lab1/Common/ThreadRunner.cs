using System.Runtime.ExceptionServices;

namespace Labs.Lab1.Common;

/// <summary>Runs a body on N dedicated OS threads created by the program (not the thread pool).</summary>
public static class ThreadRunner
{
    public static void Run(int threadCount, Action<int> body, string name = "worker")
    {
        Exception? failure = null;
        var threads = new Thread[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            int index = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    body(index);
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref failure, e, null);
                }
            })
            {
                Name = $"{name}-{index}",
                IsBackground = true
            };
        }

        foreach (var thread in threads)
            thread.Start();
        foreach (var thread in threads)
            thread.Join();

        if (failure != null)
            ExceptionDispatchInfo.Throw(failure);
    }

    /// <summary>The half-open index range [from, to) of part <paramref name="part"/> out of <paramref name="parts"/>.</summary>
    public static (long From, long To) Range(long length, int part, int parts) =>
        (length * part / parts, length * (part + 1) / parts);
}
