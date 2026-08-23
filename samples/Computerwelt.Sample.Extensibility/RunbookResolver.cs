using Computerwelt.Emulation.Bash;
using Computerwelt.Emulation.Bash.Builtins;

namespace Computerwelt.Sample.Extensibility;

/// <summary>
/// An open-ended family of commands: every runbook in the catalogue is a command.
/// </summary>
/// <remarks>
/// <para>
/// A resolver is for the case registration cannot serve: a name space the host does not
/// want to enumerate up front, because it is large, remote or generated. Here the catalogue
/// stands in for a service the host would query.
/// </para>
/// <para>
/// It is consulted last — after shell functions, registered commands and the search for a
/// script — so it cannot shadow anything. The price is that its names are invisible to
/// <c>type</c>, <c>command -v</c> and completion, which is the honest report: the host
/// cannot list them either.
/// </para>
/// </remarks>
public sealed class RunbookResolver : ICommandResolver
{
    private const string Prefix = "runbook:";

    private readonly IReadOnlyDictionary<string, string> _catalogue;

    /// <summary>Creates a resolver over a catalogue of runbooks, by name.</summary>
    public RunbookResolver(IReadOnlyDictionary<string, string> catalogue) => _catalogue = catalogue;

    /// <inheritdoc />
    public IBuiltin? Resolve(string name)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal)
            || !_catalogue.TryGetValue(name[Prefix.Length..], out var text))
        {
            // Not ours. The shell carries on and reports `command not found`, which is what
            // an unknown runbook should look like.
            return null;
        }

        return new DelegateBuiltin(name, _ => ExecResult.Ok(text), $"{name}: prints the {name[Prefix.Length..]} runbook.");
    }
}
