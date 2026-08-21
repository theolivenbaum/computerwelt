using System.Text;
using Computerwelt.Emulation.Python.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Computerwelt.Emulation.Python.SpecTests;

/// <summary>
/// Runs the Python fixture corpus under a ratchet.
/// </summary>
/// <remarks>
/// The corpus needs no harness format: a fixture is ordinary Python whose body is
/// <c>assert</c> statements, so it passes when it runs to completion without raising.
/// Fixtures that pin a <c># Raise=</c> or a traceback invert that condition.
/// <para>
/// Set <c>COMPUTERWELT_UPDATE_PYTHON_BASELINE=1</c> to rewrite the baseline after making
/// fixtures pass, and <c>COMPUTERWELT_SHOW_FAILURES=1</c> to list the failing ones by name.
/// </para>
/// </remarks>
public sealed class ConformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void Corpus_does_not_regress()
    {
        var fixtures = SpecSuite.LoadAll();
        Assert.NotEmpty(fixtures);

        var baseline = SpecSuite.LoadBaseline();
        var passing = new HashSet<string>(StringComparer.Ordinal);
        var failureReasons = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var fixture in fixtures)
        {
            if (fixture.ExpectedToFail)
            {
                continue;
            }

            var (passed, reason) = Run(fixture);

            if (passed)
            {
                passing.Add(fixture.Name);
            }
            else
            {
                failureReasons[fixture.Name] = reason;
            }
        }

        var runnable = fixtures.Count(static f => !f.ExpectedToFail);
        output.WriteLine($"python: {passing.Count}/{runnable} fixtures passing");
        WriteFailureSummary(failureReasons);

        if (Environment.GetEnvironmentVariable("COMPUTERWELT_UPDATE_PYTHON_BASELINE") == "1")
        {
            SpecSuite.SaveBaseline(passing);
            output.WriteLine($"baseline written to {SpecSuite.BaselinePath}");
            return;
        }

        var regressions = baseline.Except(passing).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            regressions.Count == 0,
            "these fixtures used to pass and no longer do:\n"
            + string.Join('\n', regressions.Select(name =>
                $"  {name}: {failureReasons.GetValueOrDefault(name, "unknown")}")));
    }

    /// <summary>Runs one fixture and decides whether it passed.</summary>
    private static (bool Passed, string Reason) Run(SpecFixture fixture)
    {
        RunResult result;

        try
        {
            var runner = new PythonRunner { FileSystem = new FixtureFileSystem() };

            // A `# mount-fs` fixture expects `root` bound to the mounted tree.
            if (fixture.Source.Contains("# mount-fs", StringComparison.Ordinal))
            {
                runner.Variables["root"] = new Computerwelt.Emulation.Python.Modules.PyPath("/mnt", runner.FileSystem);
            }

            // Fixtures marked `# call-external` exercise the host boundary, and upstream's
            // harness supplies the same named functions.
            if (fixture.Source.Contains("# call-external", StringComparison.Ordinal))
            {
                foreach (var (name, implementation) in ExternalFunctions.Create())
                {
                    runner.ExternalFunctions[name] = implementation;
                }

                // The same harness resolves a handful of non-function names to plain
                // values, which is how a host injects constants.
                foreach (var (name, value) in ExternalFunctions.Constants())
                {
                    runner.Variables[name] = value;
                }
            }

            result = runner.Run(fixture.Source, fixture.Name);
        }
        catch (Exception e)
        {
            // An escaping .NET exception is an interpreter bug, not a Python-level failure,
            // and is worth distinguishing in the summary.
            return (false, $"host {e.GetType().Name}: {Truncate(e.Message)}");
        }

        if (fixture.ExpectedRaise is { } expected)
        {
            if (result.Succeeded)
            {
                return (false, $"expected {expected}, ran clean");
            }

            return result.ExceptionType == expected
                ? (true, string.Empty)
                : (false, $"expected {expected}, got {result.ExceptionType}");
        }

        if (fixture.ExpectedTraceback is not null)
        {
            return result.Succeeded
                ? (false, "expected a traceback, ran clean")
                : (true, string.Empty);
        }

        return result.Succeeded
            ? (true, string.Empty)
            : (false, $"{result.ExceptionType}: {Truncate(result.Exception?.Message ?? result.SyntaxError?.Message ?? string.Empty)}");
    }

    private static string Truncate(string text) =>
        text.Length <= 90 ? text : text[..90] + "…";

    /// <summary>Groups failures by cause, so the biggest gaps are visible at a glance.</summary>
    private void WriteFailureSummary(Dictionary<string, string> failures)
    {
        var grouped = failures.Values
            .GroupBy(static reason => reason.Split(':')[0])
            .OrderByDescending(static group => group.Count())
            .Take(15);

        var builder = new StringBuilder("failures by kind:\n");

        foreach (var group in grouped)
        {
            builder.Append("  ").Append(group.Count().ToString().PadLeft(4))
                .Append("  ").Append(group.Key).Append('\n');
        }

        output.WriteLine(builder.ToString());

        if (Environment.GetEnvironmentVariable("COMPUTERWELT_SHOW_FAILURES") == "1")
        {
            foreach (var (name, reason) in failures.OrderBy(static f => f.Key, StringComparer.Ordinal))
            {
                output.WriteLine($"  {name}: {reason}");
            }
        }
    }
}
