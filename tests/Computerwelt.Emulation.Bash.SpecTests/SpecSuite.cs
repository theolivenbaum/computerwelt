using System.Text.Json;

namespace Computerwelt.Emulation.Bash.SpecTests;

/// <summary>Loads the conformance corpus and its ratchet baseline.</summary>
public static class SpecSuite
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    /// <summary>The directory holding the spec cases.</summary>
    public static string Root { get; } = LocateRoot();

    /// <summary>The ratchet file recording how many cases each file is expected to pass.</summary>
    public static string BaselinePath => Path.Combine(Root, "baseline.json");

    /// <summary>Loads every case, grouped by the file it came from.</summary>
    public static Dictionary<string, List<SpecCase>> LoadAll()
    {
        var result = new Dictionary<string, List<SpecCase>>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(Root, "*.test.sh", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
            result[relative] = SpecFileParser.Parse(relative, File.ReadAllText(path));
        }

        return result;
    }

    /// <summary>Loads cases for one suite directory, such as <c>bash</c>.</summary>
    public static List<SpecCase> Load(string suite)
    {
        var directory = Path.Combine(Root, suite);
        var cases = new List<SpecCase>();

        if (!Directory.Exists(directory))
        {
            return cases;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.test.sh").Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
            cases.AddRange(SpecFileParser.Parse(relative, File.ReadAllText(path)));
        }

        return cases;
    }

    /// <summary>Reads the ratchet baseline, or an empty map when it does not exist yet.</summary>
    public static Dictionary<string, int> LoadBaseline()
    {
        if (!File.Exists(BaselinePath))
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var json = File.ReadAllText(BaselinePath);
        return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>Writes a new ratchet baseline.</summary>
    public static void SaveBaseline(IDictionary<string, int> baseline)
    {
        var ordered = baseline.OrderBy(static p => p.Key, StringComparer.Ordinal)
            .ToDictionary(static p => p.Key, static p => p.Value, StringComparer.Ordinal);

        File.WriteAllText(BaselinePath, JsonSerializer.Serialize(ordered, JsonOptions) + "\n");
    }

    /// <summary>
    /// Walks up from the test assembly to find the repository's <c>tests/spec</c> directory.
    /// The corpus is data, not a compiled resource, so it is located rather than embedded —
    /// that keeps a failing case editable without a rebuild.
    /// </summary>
    private static string LocateRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "spec");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"could not locate tests/spec above {AppContext.BaseDirectory}");
    }
}
