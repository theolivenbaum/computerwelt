using System.Globalization;
using System.Text;
using Computerwelt.Emulation.Bash;
using Computerwelt.Emulation.Bash.Builtins;

namespace Computerwelt.Sample.Extensibility;

/// <summary>
/// The commands a support desk gets, contributed to the shell as one unit.
/// </summary>
/// <remarks>
/// <para>
/// An extension is how a host grants a whole domain vocabulary: a reviewer reads one
/// registration and one list of commands rather than reconstructing the capability from a
/// pile of individual calls.
/// </para>
/// <para>
/// Both commands here are stateless, as every <see cref="IBuiltin"/> must be — one instance
/// serves every execution of every session it is registered with. The tickets live in the
/// session's virtual filesystem, so two sessions using this same extension see two
/// completely separate sets of them.
/// </para>
/// </remarks>
public sealed class TicketsExtension : IShellExtension
{
    /// <summary>Where the tickets live, in the session's own filesystem.</summary>
    internal const string Store = "/var/tickets.tsv";

    /// <inheritdoc />
    public string Name => "tickets";

    /// <inheritdoc />
    public IEnumerable<IBuiltin> Builtins => [new TicketBuiltin(), new TicketExportBuiltin()];

    /// <summary>One ticket, as the store holds it.</summary>
    internal sealed record Ticket(int Id, string Status, string Title);

    /// <summary>Reads the store, which is simply absent until the first ticket is filed.</summary>
    internal static async ValueTask<List<Ticket>> ReadAsync(BuiltinContext context, CancellationToken cancellationToken)
    {
        var path = VPath.Parse(Store);

        if (!await context.FileSystem.ExistsAsync(path, cancellationToken))
        {
            return [];
        }

        var text = Encoding.UTF8.GetString(await context.FileSystem.ReadFileAsync(path, cancellationToken));

        return
        [
            .. text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t'))
                .Where(fields => fields.Length == 3)
                .Select(fields => new Ticket(int.Parse(fields[0], CultureInfo.InvariantCulture), fields[1], fields[2])),
        ];
    }

    /// <summary>Writes the store back, creating its directory the first time.</summary>
    internal static async ValueTask WriteAsync(
        BuiltinContext context,
        IEnumerable<Ticket> tickets,
        CancellationToken cancellationToken)
    {
        await context.FileSystem.CreateDirectoryAsync(VPath.Parse("/var"), recursive: true, cancellationToken);

        var text = string.Concat(tickets.Select(ticket => $"{ticket.Id}\t{ticket.Status}\t{ticket.Title}\n"));
        await context.FileSystem.WriteFileAsync(VPath.Parse(Store), Encoding.UTF8.GetBytes(text), cancellationToken);
    }

    /// <summary><c>ticket new|list|close</c> — the command someone actually types.</summary>
    private sealed class TicketBuiltin : IBuiltin
    {
        public string Name => "ticket";

        // The hint is what an LLM driving this shell is told the command can do. A command
        // with no hint is simply not mentioned to it.
        public string? LlmHint => "ticket new TITLE | ticket list [--open] | ticket close ID: the support queue.";

        public string? Help => "usage: ticket new TITLE\n       ticket list [--open]\n       ticket close ID\n";

        public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
        {
            if (context.Arguments.Count == 0 || context.Arguments[0] == "--help")
            {
                return context.Arguments.Count == 0
                    ? ExecResult.Usage(Name, "expected a subcommand: new, list or close")
                    : ExecResult.Ok(Help!);
            }

            var operands = context.Arguments.Skip(1).ToList();
            var tickets = await ReadAsync(context, cancellationToken);

            switch (context.Arguments[0])
            {
                case "new":
                {
                    var title = string.Join(' ', operands).Trim();

                    if (title.Length == 0)
                    {
                        return ExecResult.Usage(Name, "a new ticket needs a title");
                    }

                    var ticket = new Ticket(tickets.Count + 1, "open", title.Replace('\t', ' '));
                    await WriteAsync(context, [.. tickets, ticket], cancellationToken);

                    return ExecResult.Ok($"#{ticket.Id}\n");
                }

                case "list":
                {
                    var wanted = operands.Contains("--open")
                        ? tickets.Where(ticket => ticket.Status == "open")
                        : tickets;

                    var lines = string.Concat(wanted.Select(ticket => $"#{ticket.Id} [{ticket.Status}] {ticket.Title}\n"));
                    return ExecResult.Ok(lines);
                }

                case "close":
                {
                    if (operands.Count != 1 || !int.TryParse(operands[0], CultureInfo.InvariantCulture, out var id))
                    {
                        return ExecResult.Usage(Name, "close needs one ticket id");
                    }

                    if (tickets.All(ticket => ticket.Id != id))
                    {
                        // Exit status is the part a script reads, so it has to be right even
                        // when nobody is looking at the message.
                        return ExecResult.Error($"{Name}: no ticket #{id}\n", ExitCodes.Failure);
                    }

                    await WriteAsync(
                        context,
                        tickets.Select(ticket => ticket.Id == id ? ticket with { Status = "closed" } : ticket),
                        cancellationToken);

                    return ExecResult.Success;
                }

                default:
                    return ExecResult.Usage(Name, $"unknown subcommand '{context.Arguments[0]}'");
            }
        }
    }

    /// <summary><c>ticket-export</c> — the second half of the unit, so the bundle earns its name.</summary>
    private sealed class TicketExportBuiltin : IBuiltin
    {
        public string Name => "ticket-export";

        public string? LlmHint => "ticket-export: writes the queue to stdout as JSON.";

        public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
        {
            var tickets = await ReadAsync(context, cancellationToken);

            var body = string.Join(
                ",\n",
                tickets.Select(ticket =>
                    $$"""  {"id": {{ticket.Id}}, "status": "{{ticket.Status}}", "title": "{{ticket.Title}}"}"""));

            return ExecResult.Ok(tickets.Count == 0 ? "[]\n" : $"[\n{body}\n]\n");
        }
    }
}
