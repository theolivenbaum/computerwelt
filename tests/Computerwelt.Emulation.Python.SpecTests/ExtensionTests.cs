using Computerwelt.Emulation.Python.Runtime;
using Xunit;

namespace Computerwelt.Emulation.Python.SpecTests;

/// <summary>
/// Runs the fixtures for behaviour upstream Monty does not have.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from <see cref="ConformanceTests"/> deliberately. That suite is upstream's
/// corpus and defines what "correct" means; this one is the ledger of where the port has
/// gone past it, so that a reader can tell a port decision from a specification. See
/// <c>tests/monty-extensions/README.md</c>.
/// </para>
/// <para>
/// Absolute rather than ratcheted: these fixtures cover code written for this repository,
/// so there is no partial-port excuse for one of them failing.
/// </para>
/// <para>
/// <c>COMPUTERWELT_SKIP_EXTENSIONS=1</c> skips the suite, which is how you check that the
/// port still stands on upstream's corpus alone.
/// </para>
/// </remarks>
public sealed class ExtensionTests
{
    /// <summary>The notice that stands in for the corpus when the suite is switched off.</summary>
    /// <remarks>
    /// xunit 2 has no way to skip a case at run time, and a case that quietly returns is a
    /// green tick for work that did not happen. So the opt-out replaces the theory's data
    /// with this one entry: the run then shows a single case whose name says what
    /// happened, which is the honest report.
    /// </remarks>
    private const string SkipNotice = "(skipped: COMPUTERWELT_SKIP_EXTENSIONS=1)";

    private static readonly List<SpecFixture> Fixtures = Load();

    private static bool Skipped =>
        Environment.GetEnvironmentVariable("COMPUTERWELT_SKIP_EXTENSIONS") == "1";

    public static TheoryData<string> Names =>
        Skipped ? [SkipNotice] : [.. Fixtures.Select(static f => f.Name)];

    [Theory]
    [MemberData(nameof(Names))]
    public void Fixture_passes(string name)
    {
        if (name == SkipNotice)
        {
            return;
        }

        var fixture = Fixtures.Single(f => f.Name == name);
        var runner = new PythonRunner { FileSystem = new FixtureFileSystem() };

        // Two extensions are about what the host hands the program rather than about the
        // language, so the fixture asks for them by name in its header.
        if (fixture.Source.StartsWith("# argv", StringComparison.Ordinal)
            || fixture.Source.Contains("\n# argv", StringComparison.Ordinal))
        {
            runner.Arguments.AddRange([fixture.Name, "alpha", "beta"]);
        }

        if (fixture.Source.Contains("# stdin", StringComparison.Ordinal))
        {
            runner.StandardInput = "one\ntwo\nthree\n";
        }

        var result = runner.Run(fixture.Source, fixture.Name);

        Assert.True(
            result.Succeeded,
            $"{name} failed:\n{result.Traceback}\nstdout:\n{result.Stdout}");
    }

    [Fact]
    public void The_corpus_is_not_empty()
    {
        // A misplaced directory would otherwise turn the whole suite into zero silent
        // passes, which reads as green.
        Assert.NotEmpty(Fixtures);
    }

    private static List<SpecFixture> Load()
    {
        var root = Locate();

        return [.. Directory.EnumerateFiles(root, "*.py")
            .Order(StringComparer.Ordinal)
            .Select(path => SpecSuite.Parse(Path.GetFileName(path), File.ReadAllText(path)))];
    }

    /// <summary>Walks up to the repository's <c>tests/monty-extensions</c> directory.</summary>
    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "monty-extensions");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"could not locate tests/monty-extensions above {AppContext.BaseDirectory}");
    }
}
