using BenchmarkDotNet.Attributes;
using Computerwelt.Emulation.Bash;
using Computerwelt.Emulation.Bash.Parsing;

namespace Computerwelt.Benchmarks;

/// <summary>
/// The front end on its own: lexing and parsing, with no interpreter attached.
/// </summary>
/// <remarks>
/// Parsing is paid on every <c>ExecAsync</c> call, and paid again for every string a script
/// builds and evaluates, so a regression here is a regression everywhere. The scripts are
/// the corpus's own largest cases rather than invented ones.
/// </remarks>
[MemoryDiagnoser]
public class ParsingBenchmarks
{
    private static readonly string LargeScript =
        ShellCorpus.All.First(c => c.Name == "large_multiline_script").Script;

    private static readonly string PipelineScript =
        ShellCorpus.All.First(c => c.Name == "complex_pipeline_text").Script;

    private static readonly string WholeCorpus =
        string.Join('\n', ShellCorpus.All.Select(c => c.Script));

    [Benchmark]
    public int LexLargeScript() => new Lexer(LargeScript).Tokenize().Count;

    [Benchmark]
    public Script ParseLargeScript() => Parser.Parse(LargeScript);

    [Benchmark]
    public Script ParsePipeline() => Parser.Parse(PipelineScript);

    [Benchmark]
    public Script ParseWholeCorpus() => Parser.Parse(WholeCorpus);

    [Benchmark]
    public VPath NormalizePath() => VPath.Parse("/work/./src/../src/nested/deeper/file.txt");
}
