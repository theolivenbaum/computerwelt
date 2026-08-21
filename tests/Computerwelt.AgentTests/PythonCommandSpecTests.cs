using Computerwelt.Emulation.Bash;
using Computerwelt.Emulation.Bash.SpecTests;
using Xunit;

namespace Computerwelt.AgentTests;

/// <summary>
/// Bashkit's own corpus for the <c>python</c> command.
/// </summary>
/// <remarks>
/// <para>
/// Upstream ships these cases alongside the shell ones, but they exercise a command the
/// shell half does not have on its own — <c>python</c> only exists once both halves are
/// joined. So they live here rather than in <c>tests/spec</c>, where the shell suite would
/// load them and fail every one.
/// </para>
/// <para>
/// Unlike the two ratcheted suites this one is absolute: every case runs, and every case
/// that upstream does not itself skip has to pass.
/// </para>
/// </remarks>
public sealed class PythonCommandSpecTests
{
    private static readonly List<SpecCase> Cases = Load();

    public static TheoryData<string> Names => [.. Cases.Where(static c => !c.Skip).Select(static c => c.Name)];

    /// <summary>
    /// Cases upstream marks as skipped, with why.
    /// </summary>
    /// <remarks>
    /// Both reasons name a Python feature upstream's interpreter lacked when the corpus
    /// was written. <see cref="Skipped_cases_now_pass"/> runs them anyway, because a skip
    /// that has quietly become passable is coverage nobody is claiming.
    /// </remarks>
    public static TheoryData<string> SkippedNames => [.. Cases.Where(static c => c.Skip).Select(static c => c.Name)];

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Case_passes(string name)
    {
        await RunAsync(Cases.Single(c => c.Name == name));
    }

    [Theory]
    [MemberData(nameof(SkippedNames))]
    public async Task Skipped_cases_now_pass(string name)
    {
        // These are skipped upstream for want of a language feature this port has. Should
        // one ever start failing, unskipping was wrong — not the other way round.
        await RunAsync(Cases.Single(c => c.Name == name));
    }

    private static async Task RunAsync(SpecCase specCase)
    {
        var bash = Bash.CreateBuilder()
            .WithWorkingDirectory("/tmp")
            .WithPython()
            .WithLimits(ExecutionLimits.Default with { Timeout = TimeSpan.FromSeconds(10) })
            .Build();

        await bash.FileSystem.CreateDirectoryAsync("/tmp", recursive: true);
        await bash.FileSystem.CreateDirectoryAsync("/home/user", recursive: true);

        var result = await bash.ExecAsync(specCase.Script);

        Assert.Equal(specCase.ExpectedStdout, result.Stdout.ToString());

        if (specCase.ExpectedExitCode is { } expected)
        {
            Assert.Equal(expected, result.ExitCode);
        }
    }

    private static List<SpecCase> Load()
    {
        var path = Path.Combine(Root(), "spec", "python.test.sh");
        return SpecFileParser.Parse("python.test.sh", File.ReadAllText(path));
    }

    /// <summary>Walks up to this project's own directory, where the corpus lives.</summary>
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "spec")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"could not locate the corpus above {AppContext.BaseDirectory}");
    }
}
