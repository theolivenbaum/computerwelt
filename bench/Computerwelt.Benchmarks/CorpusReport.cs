using System.Diagnostics;
using System.Globalization;
using System.Text;
using Computerwelt.Emulation.Bash;

namespace Computerwelt.Benchmarks;

/// <summary>
/// The corpus run under a stopwatch rather than under BenchmarkDotNet.
/// </summary>
/// <remarks>
/// <para>
/// BenchmarkDotNet is the instrument you publish from; this is the one you work with. A
/// full harness run over 96 cases takes long enough that it stops being part of the edit
/// loop, and the question while editing is usually "did that get cheaper", which a warmed
/// stopwatch and <see cref="GC.GetAllocatedBytesForCurrentThread"/> answer in a second.
/// </para>
/// <para>
/// It also checks each case against the output upstream recorded, because a case that
/// stopped producing the right answer is not a faster case.
/// </para>
/// </remarks>
public static class CorpusReport
{
    /// <summary>Runs the corpus and prints a table sorted by cost.</summary>
    /// <param name="args">
    /// <c>--filter &lt;substring&gt;</c> narrows to matching cases, <c>--iterations &lt;n&gt;</c>
    /// sets the timed iterations per case (default 50).
    /// </param>
    public static async Task<int> RunAsync(string[] args)
    {
        var filter = ValueOf(args, "--filter");
        var iterations = int.TryParse(ValueOf(args, "--iterations"), out var n) ? n : 50;

        var cases = ShellCorpus.All
            .Where(c => filter is null || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (cases.Length == 0)
        {
            Console.Error.WriteLine($"no case matches '{filter}'");
            return 1;
        }

        var rows = new List<Row>(cases.Length);
        var mismatches = new List<string>();

        foreach (var testCase in cases)
        {
            // Warm the paths this case touches before the timed run: the first execution of
            // a case pays for JIT of whichever builtins it reaches, which is real but is not
            // what this table is about.
            for (var i = 0; i < 3; i++)
            {
                await RunOnceAsync(testCase);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = Stopwatch.StartNew();
            var output = string.Empty;
            for (var i = 0; i < iterations; i++)
            {
                output = await RunOnceAsync(testCase);
            }

            stopwatch.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            if (testCase.Expected is { } expected && output != expected)
            {
                mismatches.Add(testCase.Name);
            }

            rows.Add(new Row(
                testCase.Name,
                testCase.Category,
                stopwatch.Elapsed.TotalMicroseconds / iterations,
                (double)allocated / iterations));
        }

        Print(rows, mismatches);
        return mismatches.Count == 0 ? 0 : 1;
    }

    /// <summary>Runs matching cases in a loop, for a sampling profiler to watch.</summary>
    /// <param name="args">
    /// <c>--filter &lt;substring&gt;</c> narrows the set, <c>--seconds &lt;n&gt;</c> sets how
    /// long to keep going (default 20).
    /// </param>
    public static async Task<int> SoakAsync(string[] args)
    {
        var filter = ValueOf(args, "--filter");
        var seconds = int.TryParse(ValueOf(args, "--seconds"), out var s) ? s : 20;

        var cases = ShellCorpus.All
            .Where(c => filter is null || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var deadline = Stopwatch.StartNew();
        var runs = 0L;
        while (deadline.Elapsed.TotalSeconds < seconds)
        {
            foreach (var testCase in cases)
            {
                await RunOnceAsync(testCase);
                runs++;
            }
        }

        Console.WriteLine($"{runs} runs of {cases.Length} case(s) in {deadline.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    private static async Task<string> RunOnceAsync(BenchCase testCase)
    {
        var bash = Bash.Create();
        var result = await bash.ExecAsync(testCase.Script);
        return result.Stdout.ToString();
    }

    private static string? ValueOf(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void Print(List<Row> rows, List<string> mismatches)
    {
        var table = new StringBuilder();
        table.AppendLine($"{"case",-32} {"category",-11} {"µs/op",10} {"bytes/op",12}");
        foreach (var row in rows.OrderByDescending(r => r.Microseconds))
        {
            table.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{row.Name,-32} {row.Category,-11} {row.Microseconds,10:F2} {row.Bytes,12:N0}"));
        }

        var totalTime = rows.Sum(r => r.Microseconds);
        var totalBytes = rows.Sum(r => r.Bytes);
        table.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{"TOTAL",-32} {rows.Count + " cases",-11} {totalTime,10:F2} {totalBytes,12:N0}"));

        Console.Write(table.ToString());

        if (mismatches.Count > 0)
        {
            Console.Error.WriteLine($"\noutput mismatch in {mismatches.Count} case(s): {string.Join(", ", mismatches)}");
        }
    }

    private sealed record Row(string Name, BenchCategory Category, double Microseconds, double Bytes);
}
