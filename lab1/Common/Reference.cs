using System.Globalization;

namespace Labs.Lab1.Common;

/// <summary>
/// The true counts of numbers.txt, written by the generator while it creates the file.
/// Every task compares its result with them (the correctness check of the lab).
/// </summary>
public static class Reference
{
    private static string FilePath(Options options) => Path.Combine(Paths.DataDir(options), "numbers.ref.txt");

    public static void Save(Options options, Counts counts)
    {
        File.WriteAllLines(FilePath(options),
        [
            $"negative={counts.Negative}",
            $"zero={counts.Zero}",
            $"positive={counts.Positive}",
            $"total={counts.Total}"
        ]);
    }

    public static Counts? TryLoad(Options options)
    {
        string path = FilePath(options);
        if (!File.Exists(path))
            return null;

        var values = File.ReadAllLines(path)
            .Select(line => line.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0].Trim(), pair => long.Parse(pair[1], CultureInfo.InvariantCulture));
        return new Counts(values["negative"], values["zero"], values["positive"]);
    }
}
