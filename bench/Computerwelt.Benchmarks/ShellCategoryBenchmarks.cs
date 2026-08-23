using BenchmarkDotNet.Attributes;
using Computerwelt.Emulation.Bash;

namespace Computerwelt.Benchmarks;

/// <summary>
/// The same corpus as <see cref="ShellCorpusBenchmarks"/>, one row per category: an
/// operation runs every case in the category, so a change shows up as twelve numbers
/// instead of ninety-six.
/// </summary>
[MemoryDiagnoser]
public class ShellCategoryBenchmarks
{
    private BenchCase[] _cases = [];

    /// <summary>The categories, in upstream's order.</summary>
    public static IEnumerable<BenchCategory> Categories => Enum.GetValues<BenchCategory>();

    [ParamsSource(nameof(Categories))]
    public BenchCategory Category { get; set; }

    [GlobalSetup]
    public void Setup() => _cases = [.. ShellCorpus.All.Where(c => c.Category == Category)];

    [Benchmark]
    public async Task<int> Category_()
    {
        var total = 0;
        foreach (var testCase in _cases)
        {
            var bash = Bash.Create();
            total += (await bash.ExecAsync(testCase.Script)).ExitCode;
        }

        return total;
    }
}
