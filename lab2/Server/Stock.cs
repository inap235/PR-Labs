using System.Text.Json;

namespace Labs.Lab2.Server;

/// How many of each product the shop has. Every method holds the lock for its whole work,
/// so a check and the change after it are one step for the other threads.
public sealed class Stock
{
    /// Below this many, the provider gets an ALERT.
    public const int Low = 5;

    /// A delivery may not bring a product above this many (keeps the numbers far from int overflow).
    public const int Max = 100_000;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly int[] _counts;
    private readonly string _file;

    private Stock(int[] counts, string file)
    {
        _counts = counts;
        _file = file;
    }

    /// The stock saved in the file, or a random one (10 to 50 of each) if there is no valid file.
    public static Stock Load(string file)
    {
        int[]? counts = Read(file);
        if (counts != null)
        {
            Log.Write("server", $"stock loaded from {file}");
            return new Stock(counts, file);
        }

        counts = new int[Products.Count];
        for (int p = 0; p < counts.Length; p++)
            counts[p] = Random.Shared.Next(10, 51);
        var stock = new Stock(counts, file);
        lock (stock._lock)
            stock.Save();
        Log.Write("server", $"new random stock, saved to {file}");
        return stock;
    }

    public int[] Snapshot()
    {
        lock (_lock)
            return (int[])_counts.Clone();
    }

    /// Takes all products of the order, or nothing if one is short.
    /// left = the stock just before, for REFUSED and the alerts.
    public bool TryTake(int[] wanted, out int[] left)
    {
        lock (_lock)
        {
            left = (int[])_counts.Clone();
            for (int p = 0; p < _counts.Length; p++)
            {
                if (wanted[p] > _counts[p])
                    return false;
            }
            for (int p = 0; p < _counts.Length; p++)
                _counts[p] -= wanted[p];
            Save();
            return true;
        }
    }

    /// Adds a delivery, or nothing if a product would go above Max. after = the stock after it.
    public bool TryDeliver(int[] delivered, out int[] after)
    {
        lock (_lock)
        {
            after = (int[])_counts.Clone();
            for (int p = 0; p < _counts.Length; p++)
            {
                if (_counts[p] + delivered[p] > Max)
                    return false;
            }
            for (int p = 0; p < _counts.Length; p++)
                _counts[p] += delivered[p];
            Save();
            after = (int[])_counts.Clone();
            return true;
        }
    }

    /// Puts back the products of a cancelled order and returns the stock after it.
    public int[] Return(int[] products)
    {
        lock (_lock)
        {
            for (int p = 0; p < _counts.Length; p++)
                _counts[p] += products[p];
            Save();
            return (int[])_counts.Clone();
        }
    }

    /// Called under the lock after every change, so the file always holds the latest stock
    /// and two saves can never be written in the wrong order.
    /// Written to a .tmp file first and then renamed: a crash mid-write never leaves half a file.
    private void Save()
    {
        try
        {
            string temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Protocol.Object(_counts), Indented));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the shop goes on without saving
            Log.Write("server", $"error: could not save the stock: {e.Message}");
        }
    }

    /// The file holds { "coffee": 43, "tea": 12, "croissant": 50 }. Null if missing or invalid.
    private static int[]? Read(string file)
    {
        if (!File.Exists(file))
            return null;
        try
        {
            var json = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(file))
                       ?? throw new FormatException("the file is empty");
            var counts = new int[Products.Count];
            Array.Fill(counts, -1);
            foreach (var (name, count) in json)
            {
                int product = Products.Find(name);
                if (product < 0)
                    throw new FormatException($"unknown product '{name}'");
                if (count is < 0 or > Max)
                    throw new FormatException($"bad count {count} for {name}");
                counts[product] = count;
            }
            if (counts.Contains(-1))
                throw new FormatException("every product needs a count");
            return counts;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            Log.Write("server", $"error: cannot read {file} ({e.Message}), starting with a random stock");
            return null;
        }
    }

    public static bool FellLow(int[] before, int[] taken, int product) =>
        before[product] >= Low && before[product] - taken[product] < Low;
}
