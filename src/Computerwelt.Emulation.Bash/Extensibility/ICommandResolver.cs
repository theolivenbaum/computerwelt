namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// A last-chance lookup for a command name nothing else resolved.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BashBuilder.WithBuiltin(IBuiltin)"/> and
/// <see cref="BashBuilder.WithExtension(IShellExtension)"/> both map <i>names the host can
/// enumerate</i> to implementations. A resolver answers for a name the shell could not
/// otherwise resolve, which is what an open-ended command space — a remote tool catalogue,
/// a generated family of commands — needs when enumerating it up front is not possible.
/// </para>
/// <para>
/// It is consulted last: after shell functions, after registered commands, and after the
/// search for a script in the virtual filesystem. A resolver therefore cannot shadow
/// anything that already exists; use <see cref="BashBuilder.WithBuiltin(IBuiltin)"/> to
/// override a command deliberately.
/// </para>
/// <para>
/// Resolved names are not enumerable, so they do not appear in
/// <see cref="Bash.BuiltinNames"/>, in <c>type</c>, in <c>command -v</c> or in completion.
/// A host that wants a name to be discoverable should register it.
/// </para>
/// <para>
/// One resolver serves every execution of every session it was registered with, so an
/// implementation must be thread-safe. Returning <see langword="null"/> is the ordinary
/// answer for a name this resolver does not own.
/// </para>
/// </remarks>
public interface ICommandResolver
{
    /// <summary>Returns the command for <paramref name="name"/>, or null when it owns none.</summary>
    IBuiltin? Resolve(string name);
}
