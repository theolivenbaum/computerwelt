namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// The set of commands a session can invoke.
/// </summary>
/// <remarks>
/// <para>
/// Two lookups, deliberately kept apart. <see cref="TryGet"/> answers from the registered
/// names — the closed list the host wrote, which is what the security posture rests on.
/// <see cref="ResolveUnknown"/> asks the registered <see cref="ICommandResolver"/>s about a
/// name that nothing else matched, and is called only once the shell has already failed to
/// find a function, a registered command and a script.
/// </para>
/// <para>
/// The table is built once and shared by every execution of one session, including its
/// subshells, so it is immutable and its contents must be thread-safe.
/// </para>
/// </remarks>
public sealed class CommandTable
{
    private readonly IReadOnlyDictionary<string, IBuiltin> _builtins;
    private readonly IReadOnlyList<ICommandResolver> _resolvers;

    /// <summary>Creates a table over a registry and, optionally, a chain of resolvers.</summary>
    public CommandTable(
        IReadOnlyDictionary<string, IBuiltin> builtins,
        IReadOnlyList<ICommandResolver>? resolvers = null)
    {
        ArgumentNullException.ThrowIfNull(builtins);

        _builtins = builtins;
        _resolvers = resolvers ?? [];
    }

    /// <summary>The registered names. Resolver-supplied names are not enumerable.</summary>
    /// <remarks>
    /// A dictionary's key collection is already a read-only collection; the copy is for a
    /// host that supplied some other <see cref="IReadOnlyDictionary{TKey,TValue}"/> whose
    /// keys are only enumerable.
    /// </remarks>
    public IReadOnlyCollection<string> Names =>
        _builtins.Keys as IReadOnlyCollection<string> ?? [.. _builtins.Keys];

    /// <summary>True when <paramref name="name"/> is registered.</summary>
    public bool Contains(string name) => _builtins.ContainsKey(name);

    /// <summary>Looks a registered command up.</summary>
    public bool TryGet(string name, out IBuiltin builtin) => _builtins.TryGetValue(name, out builtin!);

    /// <summary>
    /// Asks the resolvers, in registration order, about a name nothing else resolved.
    /// </summary>
    public IBuiltin? ResolveUnknown(string name)
    {
        foreach (var resolver in _resolvers)
        {
            if (resolver.Resolve(name) is { } builtin)
            {
                return builtin;
            }
        }

        return null;
    }
}

/// <summary>An <see cref="ICommandResolver"/> backed by a delegate.</summary>
internal sealed class DelegateCommandResolver : ICommandResolver
{
    private readonly Func<string, IBuiltin?> _resolve;

    public DelegateCommandResolver(Func<string, IBuiltin?> resolve) => _resolve = resolve;

    public IBuiltin? Resolve(string name) => _resolve(name);
}
