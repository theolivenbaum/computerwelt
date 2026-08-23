namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// A related set of commands registered as one unit.
/// </summary>
/// <remarks>
/// <para>
/// A host with a domain vocabulary — a ticket system, a build tool, a data catalogue —
/// ships it as one extension rather than as a loose pile of
/// <see cref="BashBuilder.WithBuiltin(IBuiltin)"/> calls, so the capability can be granted
/// or withheld as a whole and named in a review.
/// </para>
/// <para>
/// An extension contributes commands and nothing else. It cannot widen the sandbox in any
/// other way: it gets no hook into the filesystem, the limits or the network, because a
/// unit of capability that could quietly adjust those would not be reviewable by reading
/// its command list.
/// </para>
/// <para>
/// Its commands follow the same override rule as any other registration — a later one
/// replaces an earlier one of the same name.
/// </para>
/// </remarks>
public interface IShellExtension
{
    /// <summary>The extension's name, for diagnostics and for a host's own inventory.</summary>
    string Name { get; }

    /// <summary>The commands this extension contributes.</summary>
    IEnumerable<IBuiltin> Builtins { get; }
}
