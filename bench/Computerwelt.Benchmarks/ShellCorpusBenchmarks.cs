using BenchmarkDotNet.Attributes;
using Computerwelt.Emulation.Bash;

namespace Computerwelt.Benchmarks;

/// <summary>
/// Upstream's corpus, one row per case.
/// </summary>
/// <remarks>
/// A session is built inside the measured region because upstream's in-process runner does
/// the same — <c>run_bashkit</c> calls <c>Bash::builder().build()</c> per run — so the cost
/// of standing a sandbox up is part of what a caller pays and part of what is reported.
/// <see cref="ShellCategoryBenchmarks"/> is the same corpus at category granularity, for
/// when 96 rows is more than the question needs.
/// </remarks>
[MemoryDiagnoser]
public class ShellCorpusBenchmarks
{
    /// <summary>Every case, wrapped so the report labels rows by name.</summary>
    public static IEnumerable<CaseArgument> Cases => ShellCorpus.All.Select(c => new CaseArgument(c));

    [Benchmark]
    [ArgumentsSource(nameof(Cases))]
    public async Task<int> Case(CaseArgument argument)
    {
        var bash = Bash.Create();
        var result = await bash.ExecAsync(argument.Case.Script);
        return result.ExitCode;
    }
}
