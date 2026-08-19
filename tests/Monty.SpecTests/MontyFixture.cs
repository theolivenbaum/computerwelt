namespace Monty.SpecTests;

/// <summary>One fixture from the Monty corpus.</summary>
public sealed record MontyFixture
{
    /// <summary>The file name, relative to the corpus root.</summary>
    public required string Name { get; init; }

    /// <summary>The Python source.</summary>
    public required string Source { get; init; }

    /// <summary>True when upstream itself marks the fixture as expected to fail.</summary>
    public bool ExpectedToFail { get; init; }

    /// <summary>
    /// The exception the fixture pins with a <c># Raise=</c> comment, if any. Its presence
    /// inverts the pass condition: the file is expected to raise, not to run clean.
    /// </summary>
    public string? ExpectedRaise { get; init; }

    /// <summary>The traceback the fixture pins in a trailing docstring, if any.</summary>
    public string? ExpectedTraceback { get; init; }

    /// <summary>True when the fixture passes by running to completion without raising.</summary>
    public bool ExpectsCleanRun => ExpectedRaise is null && ExpectedTraceback is null;

    /// <inheritdoc />
    public override string ToString() => Name;
}
