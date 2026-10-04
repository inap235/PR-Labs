namespace Labs.Lab1.Common;

/// <summary>Command-line options of the form <c>--name value</c>.</summary>
public sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{args[i]}'. Options look like --name value.");

            string name = args[i][2..];
            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            options._values[name] = hasValue ? args[++i] : "true";
        }
        return options;
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public string Get(string name, string defaultValue) => _values.GetValueOrDefault(name, defaultValue);

    public int GetInt(string name, int defaultValue, int min = int.MinValue)
    {
        if (!_values.TryGetValue(name, out var text))
            return defaultValue;
        if (!int.TryParse(text, out int value) || value < min)
            throw new ArgumentException($"--{name} must be an integer >= {min}, got '{text}'.");
        return value;
    }

    public long GetLong(string name, long defaultValue, long min = long.MinValue)
    {
        if (!_values.TryGetValue(name, out var text))
            return defaultValue;
        if (!long.TryParse(text, out long value) || value < min)
            throw new ArgumentException($"--{name} must be an integer >= {min}, got '{text}'.");
        return value;
    }
}
