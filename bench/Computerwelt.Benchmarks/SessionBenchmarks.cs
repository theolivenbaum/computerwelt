using BenchmarkDotNet.Attributes;
using Computerwelt.Emulation.Bash;

namespace Computerwelt.Benchmarks;

/// <summary>
/// What standing a sandbox up costs, separated from what running a script in it costs.
/// </summary>
/// <remarks>
/// Every case in <see cref="ShellCorpusBenchmarks"/> pays this, and a caller serving one
/// script per session pays nothing else, so it is worth its own row: if building a session
/// dominates <c>startup_echo</c>, the corpus is measuring registration, not the shell.
/// </remarks>
[MemoryDiagnoser]
public class SessionBenchmarks
{
    [Benchmark(Baseline = true)]
    public Bash Build() => Bash.Create();

    [Benchmark]
    public Bash BuildWithPython() => Bash.CreateBuilder().WithPython().Build();

    [Benchmark]
    public async Task<int> BuildAndEcho()
    {
        var bash = Bash.Create();
        return (await bash.ExecAsync("echo hello")).ExitCode;
    }

    [Benchmark]
    public async Task<int> EchoOnWarmSession() => (await _warm.ExecAsync("echo hello")).ExitCode;

    private readonly Bash _warm = Bash.Create();
}
