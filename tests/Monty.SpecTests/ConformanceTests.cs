using System.Text;
using Monty.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Monty.SpecTests;

/// <summary>
/// Runs the Monty fixture corpus under a ratchet.
/// </summary>
/// <remarks>
/// The corpus needs no harness format: a fixture is ordinary Python whose body is
/// <c>assert</c> statements, so it passes when it runs to completion without raising.
/// Fixtures that pin a <c># Raise=</c> or a traceback invert that condition.
/// </remarks>
public sealed class ConformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void Corpus_does_not_regress()
    {
        var fixtures = MontySuite.LoadAll();
        Assert.NotEmpty(fixtures);

        var baseline = MontySuite.LoadBaseline();
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
        output.WriteLine($"monty: {passing.Count}/{runnable} fixtures passing");
        WriteFailureSummary(failureReasons);

        if (Environment.GetEnvironmentVariable("MONTY_UPDATE_BASELINE") == "1")
        {
            MontySuite.SaveBaseline(passing);
            output.WriteLine($"baseline written to {MontySuite.BaselinePath}");
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
    private static (bool Passed, string Reason) Run(MontyFixture fixture)
    {
        RunResult result;

        try
        {
            result = new MontyRunner().Run(fixture.Source, fixture.Name);
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

        if (Environment.GetEnvironmentVariable("MONTY_SHOW_FAILURES") == "1")
        {
            foreach (var (name, reason) in failures.OrderBy(static f => f.Key, StringComparer.Ordinal).Take(40))
            {
                output.WriteLine($"  {name}: {reason}");
            }
        }
    }
}
