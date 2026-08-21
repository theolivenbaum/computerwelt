namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// A command implemented in managed code.
/// </summary>
/// <remarks>
/// <para>
/// Every command here is a builtin — there is no <c>PATH</c> lookup and no process
/// spawn. That is the whole security model: a script can only invoke what the host has
/// registered.
/// </para>
/// <para>
/// Implementations are registered once and shared across every execution, so they must be
/// stateless and thread-safe. Everything per-invocation arrives in
/// <see cref="BuiltinContext"/>.
/// </para>
/// </remarks>
public interface IBuiltin
{
    /// <summary>The command's name, as it is typed.</summary>
    string Name { get; }

    /// <summary>Runs the command.</summary>
    ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// A one-line capability summary included in the LLM system prompt. Return
    /// <see langword="null"/> to omit the command from the prompt.
    /// </summary>
    string? LlmHint => null;

    /// <summary>Text shown for <c>--help</c>.</summary>
    string? Help => null;
}
