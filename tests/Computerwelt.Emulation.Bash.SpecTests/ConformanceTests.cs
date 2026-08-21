using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Computerwelt.Emulation.Bash.SpecTests;

/// <summary>
/// Runs the conformance corpus and enforces the ratchet.
/// </summary>
/// <remarks>
/// <para>
/// The port is incomplete by construction, so a plain "every case must pass" assertion
/// would be red for the whole project and therefore useless. Instead <c>baseline.json</c>
/// records how many cases each file currently passes, and the suite fails only on a
/// <i>regression</i>. That gives a signal that is meaningful from day one and gets
/// strictly stronger as the port advances.
/// </para>
/// <para>
/// Set <c>COMPUTERWELT_UPDATE_BASH_BASELINE=1</c> to rewrite the baseline after making cases pass.
/// Never lower a number by hand.
/// </para>
/// </remarks>
public sealed class ConformanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Corpus_does_not_regress()
    {
        var files = SpecSuite.LoadAll();
        Assert.NotEmpty(files);

        var baseline = SpecSuite.LoadBaseline();
        var current = new Dictionary<string, int>(StringComparer.Ordinal);
        var regressions = new StringBuilder();
        var totalRun = 0;
        var totalPassed = 0;
        var totalSkipped = 0;
        var allFailures = new List<SpecOutcome>();

        var filter = Environment.GetEnvironmentVariable("BASHKIT_SPEC_FILTER");

        foreach (var (file, cases) in files.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            if (filter is { Length: > 0 } && !file.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                current[file] = baseline.GetValueOrDefault(file, 0);
                continue;
            }

            var passed = 0;
            var failures = new List<SpecOutcome>();

            foreach (var specCase in cases)
            {
                if (specCase.Skip)
                {
                    totalSkipped++;
                    continue;
                }

                totalRun++;
                var outcome = await SpecRunner.RunAsync(specCase);

                if (outcome.Passed)
                {
                    passed++;
                    totalPassed++;
                }
                else
                {
                    failures.Add(outcome);
                    allFailures.Add(outcome);
                }
            }

            current[file] = passed;

            var expected = baseline.GetValueOrDefault(file, 0);
            if (passed >= expected)
            {
                continue;
            }

            regressions.Append(file).Append(": expected at least ").Append(expected)
                .Append(" passing, got ").Append(passed).Append('\n');

            foreach (var failure in failures.Take(3))
            {
                regressions.Append(failure.Describe()).Append("\n\n");
            }
        }

        if (filter is { Length: > 0 })
        {
            foreach (var failure in allFailures.Take(int.TryParse(Environment.GetEnvironmentVariable("BASHKIT_SPEC_SHOW"), out var show) ? show : 10))
            {
                output.WriteLine(failure.Describe());
                output.WriteLine(new string('-', 60));
            }
        }

        output.WriteLine($"spec: {totalPassed}/{totalRun} passing ({totalSkipped} skipped)");
        WriteBreakdown(output, current, files);

        if (Environment.GetEnvironmentVariable("COMPUTERWELT_UPDATE_BASH_BASELINE") == "1")
        {
            SpecSuite.SaveBaseline(current);
            output.WriteLine($"baseline written to {SpecSuite.BaselinePath}");
            return;
        }

        Assert.True(regressions.Length == 0, "conformance regressed:\n" + regressions);
    }

    private static void WriteBreakdown(ITestOutputHelper output, Dictionary<string, int> passed, Dictionary<string, List<SpecCase>> files)
    {
        foreach (var (file, cases) in files.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            var runnable = cases.Count(static c => !c.Skip);
            if (runnable == 0)
            {
                continue;
            }

            var count = passed.GetValueOrDefault(file, 0);
            output.WriteLine($"  {file,-56} {count,4}/{runnable,-4} {(count == runnable ? "" : "*")}");
        }
    }
}
