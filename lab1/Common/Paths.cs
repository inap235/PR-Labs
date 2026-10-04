namespace Labs.Lab1.Common;

/// <summary>Locations of the lab's data files.</summary>
public static class Paths
{
    /// <summary>
    /// The lab1 folder, found by walking up from the working directory and from the executable,
    /// so the program finds its data both with <c>dotnet run</c> and when started from the IDE.
    /// </summary>
    public static string Lab1Dir()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "lab1", "Lab1.cs")))
                    return Path.Combine(dir.FullName, "lab1");
                if (File.Exists(Path.Combine(dir.FullName, "Lab1.cs")))
                    return dir.FullName;
            }
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "lab1");
    }

    public static string DataDir(Options options) => options.Get("data") ?? Path.Combine(Lab1Dir(), "data");

    public static string NumbersFile(Options options) => Path.Combine(DataDir(options), "numbers.txt");

    public static string PartsDir(Options options) => Path.Combine(DataDir(options), "parts");

    public static string RequireFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"File '{path}' not found. Run 'lab1 gen' (and 'lab1 split' for Task 2) first.");
        return path;
    }

    /// <summary>Opens a file for reading without FileStream's own buffer: the tasks use their own buffers.</summary>
    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.SequentialScan);
}
