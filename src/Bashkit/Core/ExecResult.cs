namespace Bashkit;

/// <summary>
/// The outcome of executing a command, pipeline or script.
/// </summary>
/// <remarks>
/// Output is returned as a value rather than written to an ambient stream. That is what
/// makes pipelines, command substitution and multi-tenant isolation work without any
/// global state.
/// </remarks>
public sealed record ExecResult
{
    /// <summary>Standard output produced by the command.</summary>
    public StreamData Stdout { get; init; }

    /// <summary>Standard error produced by the command.</summary>
    public StreamData Stderr { get; init; }

    /// <summary>The process exit status.</summary>
    public int ExitCode { get; init; }

    /// <summary>A pending non-local transfer, if the command requested one.</summary>
    public ControlFlow ControlFlow { get; init; }

    /// <summary>True when <see cref="Stdout"/> was cut short by the output-size limit.</summary>
    public bool StdoutTruncated { get; init; }

    /// <summary>True when <see cref="Stderr"/> was cut short by the output-size limit.</summary>
    public bool StderrTruncated { get; init; }

    /// <summary>
    /// True when a non-zero <see cref="ExitCode"/> came from the left side of an
    /// <c>&amp;&amp;</c>/<c>||</c> list and must therefore not trigger <c>set -e</c>.
    /// </summary>
    public bool ErrExitSuppressed { get; init; }

    /// <summary>
    /// True once the <c>ERR</c> trap has fired for this failure.
    /// </summary>
    /// <remarks>
    /// A failing result travels up through the list, the loop and the script, each of which
    /// would otherwise fire the trap again for the same command.
    /// </remarks>
    public bool ErrTrapHandled { get; init; }

    /// <summary>A successful, silent result.</summary>
    public static ExecResult Success { get; } = new();

    /// <summary>A successful result carrying <paramref name="stdout"/>.</summary>
    public static ExecResult Ok(StreamData stdout) => new() { Stdout = stdout };

    /// <summary>A failed result carrying <paramref name="stderr"/> and <paramref name="exitCode"/>.</summary>
    public static ExecResult Error(StreamData stderr, int exitCode = 1) =>
        new() { Stderr = stderr, ExitCode = exitCode };

    /// <summary>A result carrying only an exit status.</summary>
    public static ExecResult FromExitCode(int exitCode) => new() { ExitCode = exitCode };

    /// <summary>
    /// A usage error in the conventional shape <c>&lt;command&gt;: &lt;message&gt;</c> with
    /// a trailing newline, exiting with <paramref name="exitCode"/> (2 by default, as
    /// coreutils does for bad options).
    /// </summary>
    public static ExecResult Usage(string command, string message, int exitCode = ExitCodes.Usage) =>
        Error($"{command}: {message}\n", exitCode);

    /// <summary>True when the command succeeded.</summary>
    public bool IsSuccess => ExitCode == 0;
}
