namespace Computerwelt.Benchmarks;

/// <summary>
/// A case as BenchmarkDotNet sees it: the row label is the case's name, not a dump of its
/// script, which is what keeps a 96-row table readable.
/// </summary>
public sealed class CaseArgument(BenchCase testCase)
{
    /// <summary>The case being measured.</summary>
    public BenchCase Case { get; } = testCase;

    /// <inheritdoc />
    public override string ToString() => Case.Name;
}
