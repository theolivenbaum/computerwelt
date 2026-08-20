namespace Computerwelt.Emulation.Bash.SpecTests;

/// <summary>One test case parsed from a <c>.test.sh</c> file.</summary>
public sealed record SpecCase
{
    /// <summary>The case's name, from its <c>###</c> header.</summary>
    public required string Name { get; init; }

    /// <summary>The file the case came from, relative to the spec root.</summary>
    public required string File { get; init; }

    /// <summary>A one-line description from the leading comment, if any.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The script to run.</summary>
    public string Script { get; init; } = string.Empty;

    /// <summary>The expected standard output, with a trailing newline when non-empty.</summary>
    public string ExpectedStdout { get; init; } = string.Empty;

    /// <summary>The expected exit status, when the case pins one.</summary>
    public int? ExpectedExitCode { get; init; }

    /// <summary>True when the case is marked to be skipped.</summary>
    public bool Skip { get; init; }

    /// <summary>Why the case is skipped.</summary>
    public string? SkipReason { get; init; }

    /// <summary>True when the case documents a deliberate divergence from real bash.</summary>
    public bool BashDiff { get; init; }

    /// <summary>True when the case needs a deterministic clock.</summary>
    public bool PausedTime { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{File}::{Name}";
}
