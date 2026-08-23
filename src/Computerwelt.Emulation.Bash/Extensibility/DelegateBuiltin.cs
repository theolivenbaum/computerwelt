namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// A command implemented by a delegate.
/// </summary>
/// <remarks>
/// The shortest way to add one command. Anything with state, options worth parsing or help
/// worth writing is better as an <see cref="IBuiltin"/> class; this exists so that the
/// simple case does not have to be one.
/// </remarks>
public sealed class DelegateBuiltin : IBuiltin
{
    private readonly Func<BuiltinContext, CancellationToken, ValueTask<ExecResult>> _run;

    /// <summary>Creates a command from an asynchronous delegate.</summary>
    /// <param name="name">The name the command is typed as.</param>
    /// <param name="run">
    /// The implementation. It is shared across every execution of every session it is
    /// registered with, so it must be thread-safe and hold no per-invocation state —
    /// everything one invocation can see arrives in its <see cref="BuiltinContext"/>.
    /// </param>
    /// <param name="llmHint">The one-line capability summary, or null to omit the command from the prompt.</param>
    /// <param name="help">The text <c>--help</c> shows, if the command answers it.</param>
    public DelegateBuiltin(
        string name,
        Func<BuiltinContext, CancellationToken, ValueTask<ExecResult>> run,
        string? llmHint = null,
        string? help = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(run);

        Name = name;
        _run = run;
        LlmHint = llmHint;
        Help = help;
    }

    /// <summary>Creates a command from a synchronous delegate.</summary>
    public DelegateBuiltin(
        string name,
        Func<BuiltinContext, ExecResult> run,
        string? llmHint = null,
        string? help = null)
        : this(
            name,
            (context, _) => ValueTask.FromResult((run ?? throw new ArgumentNullException(nameof(run)))(context)),
            llmHint,
            help)
    {
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string? LlmHint { get; }

    /// <inheritdoc />
    public string? Help { get; }

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default) =>
        _run(context, cancellationToken);
}
