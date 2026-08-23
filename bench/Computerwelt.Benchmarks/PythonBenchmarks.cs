using BenchmarkDotNet.Attributes;
using Computerwelt.Emulation.Python;
using Computerwelt.Emulation.Python.Compilation;
using Computerwelt.Emulation.Python.Parsing;

namespace Computerwelt.Benchmarks;

/// <summary>
/// The other half of the sandbox: parse, compile and run, and each stage on its own.
/// </summary>
/// <remarks>
/// Monty's shape — a compiler and a bytecode VM — is what the port keeps, so the three
/// stages are worth separating: a program an agent writes is compiled once and run once,
/// which makes compilation a first-class cost rather than an amortised one.
/// </remarks>
[MemoryDiagnoser]
public class PythonBenchmarks
{
    private const string Fibonacci = """
        def fib(n):
            if n < 2:
                return n
            return fib(n - 1) + fib(n - 2)

        result = fib(18)
        """;

    private const string TextProcessing = """
        lines = [f'item {i} value {i * 7 % 31}' for i in range(500)]
        counts = {}
        for line in lines:
            key = line.split(' ')[3]
            counts[key] = counts.get(key, 0) + 1
        total = sum(counts.values())
        top = sorted(counts.items(), key=lambda kv: -kv[1])[:5]
        """;

    private const string NumericLoop = """
        total = 0
        for i in range(5000):
            total += i * 2 % 7
        """;

    private const string Calls = """
        def add(a, b):
            return a + b

        total = 0
        for i in range(2000):
            total = add(total, i)
        """;

    private const string Startup = "x = 1\n";

    [Benchmark]
    public bool RunHelloWorld() => new PythonRunner().Run(Startup).Succeeded;

    [Benchmark]
    public bool RunFibonacci() => new PythonRunner().Run(Fibonacci).Succeeded;

    [Benchmark]
    public bool RunTextProcessing() => new PythonRunner().Run(TextProcessing).Succeeded;

    [Benchmark]
    public bool RunNumericLoop() => new PythonRunner().Run(NumericLoop).Succeeded;

    [Benchmark]
    public bool RunCalls() => new PythonRunner().Run(Calls).Succeeded;

    [Benchmark]
    public PyModule ParseTextProcessing() => Parser.Parse(TextProcessing);

    [Benchmark]
    public CodeObject CompileTextProcessing() => Compiler.CompileModule(Parser.Parse(TextProcessing), "<bench>");
}
