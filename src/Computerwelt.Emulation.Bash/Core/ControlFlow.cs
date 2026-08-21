namespace Computerwelt.Emulation.Bash;

/// <summary>
/// A non-local control transfer requested by a builtin or keyword.
/// </summary>
/// <remarks>
/// Upstream models this as an enum returned inside <c>ExecResult</c> rather than as a
/// panic/exception, so that loop and function frames can inspect and consume it. The port
/// keeps that shape: control flow is data, not an exception, because <c>break 2</c> must
/// decrement a counter as it unwinds.
/// </remarks>
public readonly record struct ControlFlow
{
    private ControlFlow(ControlFlowKind kind, int level)
    {
        Kind = kind;
        Level = level;
    }

    /// <summary>What kind of transfer was requested.</summary>
    public ControlFlowKind Kind { get; }

    /// <summary>
    /// For <see cref="ControlFlowKind.Break"/> and <see cref="ControlFlowKind.Continue"/>,
    /// the number of enclosing loops still to unwind. For <see cref="ControlFlowKind.Return"/>
    /// and <see cref="ControlFlowKind.Exit"/>, the exit code.
    /// </summary>
    public int Level { get; }

    /// <summary>Normal completion.</summary>
    public static ControlFlow None => default;

    /// <summary>Break out of <paramref name="levels"/> enclosing loops.</summary>
    public static ControlFlow Break(int levels = 1) => new(ControlFlowKind.Break, Math.Max(1, levels));

    /// <summary>Continue the <paramref name="levels"/>-th enclosing loop.</summary>
    public static ControlFlow Continue(int levels = 1) => new(ControlFlowKind.Continue, Math.Max(1, levels));

    /// <summary>Return from the current function or sourced script with <paramref name="code"/>.</summary>
    public static ControlFlow Return(int code) => new(ControlFlowKind.Return, code);

    /// <summary>Terminate the whole script with <paramref name="code"/>.</summary>
    public static ControlFlow Exit(int code) => new(ControlFlowKind.Exit, code);

    /// <summary>True when no transfer was requested.</summary>
    public bool IsNone => Kind == ControlFlowKind.None;

    /// <summary>
    /// Consumes one level of a break/continue. Returns <see cref="None"/> when this frame
    /// absorbs the transfer, or a decremented signal to propagate further out.
    /// </summary>
    public ControlFlow Unwind() =>
        Kind is ControlFlowKind.Break or ControlFlowKind.Continue && Level > 1
            ? new ControlFlow(Kind, Level - 1)
            : None;
}

/// <summary>The kinds of non-local transfer a command can request.</summary>
public enum ControlFlowKind
{
    /// <summary>Normal completion.</summary>
    None = 0,

    /// <summary>The <c>break</c> builtin.</summary>
    Break,

    /// <summary>The <c>continue</c> builtin.</summary>
    Continue,

    /// <summary>The <c>return</c> builtin.</summary>
    Return,

    /// <summary>The <c>exit</c> builtin.</summary>
    Exit,
}
