using System.Text.Json;
using System.Text.RegularExpressions;

namespace Monty.SpecTests;

/// <summary>Loads the Monty fixture corpus.</summary>
public static partial class MontySuite
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The directory holding the fixtures.</summary>
    public static string Root { get; } = Locate();

    /// <summary>The ratchet file.</summary>
    public static string BaselinePath => Path.Combine(Root, "baseline.json");

    /// <summary>Loads every fixture.</summary>
    public static List<MontyFixture> LoadAll()
    {
        var fixtures = new List<MontyFixture>();

        foreach (var path in Directory.EnumerateFiles(Root, "*.py").Order(StringComparer.Ordinal))
        {
            fixtures.Add(Parse(Path.GetFileName(path), File.ReadAllText(path)));
        }

        return fixtures;
    }

    /// <summary>Parses a fixture's directives out of its source.</summary>
    public static MontyFixture Parse(string name, string source)
    {
        var expectedToFail = XfailPattern().IsMatch(source);
        var raise = RaisePattern().Match(source);

        // A trailing docstring introduced by TRACEBACK: pins the expected traceback.
        var traceback = TracebackPattern().Match(source);

        return new MontyFixture
        {
            Name = name,
            Source = source,
            ExpectedToFail = expectedToFail,
            ExpectedRaise = raise.Success ? raise.Groups[1].Value : null,
            ExpectedTraceback = traceback.Success ? traceback.Groups[1].Value.Trim() : null,
        };
    }

    /// <summary>Reads the ratchet baseline.</summary>
    public static HashSet<string> LoadBaseline()
    {
        if (!File.Exists(BaselinePath))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var names = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(BaselinePath)) ?? [];
        return new HashSet<string>(names, StringComparer.Ordinal);
    }

    /// <summary>Writes a new ratchet baseline.</summary>
    public static void SaveBaseline(IEnumerable<string> passing) =>
        File.WriteAllText(
            BaselinePath,
            JsonSerializer.Serialize(passing.Order(StringComparer.Ordinal).ToList(), JsonOptions) + "\n");

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "monty-spec");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"could not locate tests/monty-spec above {AppContext.BaseDirectory}");
    }

    [GeneratedRegex(@"^#\s*xfail\s*=", RegexOptions.Multiline)]
    private static partial Regex XfailPattern();

    [GeneratedRegex(@"^#\s*Raise\s*=\s*(\w+)", RegexOptions.Multiline)]
    private static partial Regex RaisePattern();

    [GeneratedRegex(@"TRACEBACK:\s*\n(.*?)\n\s*""""""", RegexOptions.Singleline)]
    private static partial Regex TracebackPattern();
}
