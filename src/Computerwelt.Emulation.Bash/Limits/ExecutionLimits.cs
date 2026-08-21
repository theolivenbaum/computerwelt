namespace Computerwelt.Emulation.Bash;

/// <summary>
/// Per-execution resource caps. Defaults mirror upstream <c>limits.rs</c>.
/// </summary>
/// <remarks>
/// These are the sandbox's teeth. A hostile or merely runaway script must terminate in
/// bounded time and bounded memory, so every one of these is enforced during evaluation
/// rather than checked after the fact.
/// </remarks>
public sealed record ExecutionLimits
{
    /// <summary>The defaults: safe for untrusted input.</summary>
    public static ExecutionLimits Default { get; } = new();

    /// <summary>Maximum number of commands executed. Default 10,000.</summary>
    public int MaxCommands { get; init; } = 10_000;

    /// <summary>Maximum iterations of any single loop. Default 10,000.</summary>
    public int MaxLoopIterations { get; init; } = 10_000;

    /// <summary>
    /// Maximum loop iterations summed across every loop. Without this, <c>n</c> nested
    /// loops permit <c>MaxLoopIterations^n</c> total work.
    /// </summary>
    public int MaxTotalLoopIterations { get; init; } = 1_000_000;

    /// <summary>Maximum shell function recursion depth. Default 100.</summary>
    public int MaxFunctionDepth { get; init; } = 100;

    /// <summary>Maximum nesting depth for compound commands and expansions. Default 100.</summary>
    public int MaxNestingDepth { get; init; } = 100;

    /// <summary>Wall-clock budget for the whole execution. Default 30s.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Wall-clock budget for parsing alone. Default 5s.</summary>
    public TimeSpan ParserTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum script size in bytes. Default 10 MB.</summary>
    public long MaxInputBytes { get; init; } = 10_000_000;

    /// <summary>Maximum bytes retained on stdout or stderr. Default 10 MB.</summary>
    public long MaxOutputBytes { get; init; } = 10_000_000;

    /// <summary>Abstract work units shared by parser, interpreter and builtins. Default 100M.</summary>
    public long MaxWorkUnits { get; init; } = 100_000_000;

    /// <summary>Parser fuel, consumed per token and per production. Default 10M.</summary>
    public long MaxParserFuel { get; init; } = 10_000_000;

    /// <summary>Maximum expansions produced by a single brace expansion. Default 10,000.</summary>
    public int MaxBraceExpansionItems { get; init; } = 10_000;

    /// <summary>Maximum paths a single glob may match. Default 10,000.</summary>
    public int MaxGlobMatches { get; init; } = 10_000;

    /// <summary>A permissive profile for trusted scripts.</summary>
    public static ExecutionLimits Permissive { get; } = new()
    {
        MaxCommands = 1_000_000,
        MaxLoopIterations = 1_000_000,
        MaxTotalLoopIterations = 100_000_000,
        MaxFunctionDepth = 1_000,
        Timeout = TimeSpan.FromMinutes(10),
        ParserTimeout = TimeSpan.FromSeconds(60),
        MaxOutputBytes = 100_000_000,
        MaxWorkUnits = 10_000_000_000,
    };

    /// <summary>A tight profile for short, hostile-by-assumption input.</summary>
    public static ExecutionLimits Strict { get; } = new()
    {
        MaxCommands = 1_000,
        MaxLoopIterations = 1_000,
        MaxTotalLoopIterations = 10_000,
        MaxFunctionDepth = 25,
        Timeout = TimeSpan.FromSeconds(5),
        ParserTimeout = TimeSpan.FromSeconds(1),
        MaxInputBytes = 100_000,
        MaxOutputBytes = 1_000_000,
        MaxWorkUnits = 1_000_000,
    };
}
