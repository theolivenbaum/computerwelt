using Computerwelt;
using Computerwelt.Emulation.Bash;
using Computerwelt.Emulation.Python;
using Computerwelt.Emulation.Python.Runtime;
using Computerwelt.Sample.Extensibility;

// A support-desk sandbox: the shell gets a domain vocabulary, Python gets two libraries,
// and one command is taken away. Everything a script here can reach is on this page.
var session = Bash.CreateBuilder()
    .WithWorkingDirectory("/")
    .WithUsername("desk")

    // A related set of commands, granted as one unit.
    .WithExtension(new TicketsExtension())

    // One command, straight from a delegate.
    .WithBuiltin(
        "queue-size",
        context => ExecResult.Ok($"{context.State.Get("QUEUE_SIZE") ?? "0"}\n"),
        llmHint: "queue-size: how many tickets are waiting.")

    // A name space too large to enumerate, resolved on demand and consulted last.
    .WithCommandResolver(new RunbookResolver(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["vpn"] = "1. Reconnect on 5 GHz.\n2. Re-issue the certificate.\n",
        ["coffee"] = "1. Descale.\n2. Escalate to facilities.\n",
    }))

    // Withheld: the session simply has no such command, and no script can discover one.
    .WithoutBuiltins("tar", "curl", "wget")

    // The Python command, with the host's own libraries importable from it.
    .WithPython(new PythonOptions
    {
        // Two libraries — one written in C# over the sandbox's filesystem, one in Python.
        Libraries = [PythonLibraries.Tickets(), PythonLibraries.Formatting()],

        // And two functions implemented in C#, handed the live environment on every call.
        HostFunctions = new Dictionary<string, Func<PythonHostContext, PyObject[], PyObject>>(
            PythonLibraries.HostFunctions(), StringComparer.Ordinal),
    })

    .Build();

const string Script = """
    echo '== filing tickets =='
    ticket new "Coffee machine is down"
    ticket new "VPN drops on Wi-Fi"
    ticket new "Badge reader is slow"
    ticket close 1

    echo '== the shell =='
    ticket list
    QUEUE_SIZE=$(ticket list --open | wc -l)
    queue-size

    echo
    echo '== python, over the same filesystem =='
    python - <<'PY'
    import tickets, formatting

    rows = [(t['id'], t['status'], t['title']) for t in tickets.all()]
    print(formatting.table(['ID', 'STATUS', 'TITLE'], rows))

    open('/report.txt', 'w').write(formatting.bullets(t['title'] for t in tickets.all() if t['status'] == 'open') + '\n')
    PY

    echo
    echo '== the shell reads what python wrote =='
    cat /report.txt

    echo
    echo '== C# functions, over the live environment =='
    mkdir -p /srv
    cd /srv
    python -c "print(whereami())"
    cd /
    python -c "print(whereami(), disk_usage('/var'), 'bytes in /var')"

    echo
    echo '== a resolved command =='
    runbook:vpn

    echo
    echo '== and one that was withheld =='
    tar --version || echo "exit status $?"
    """;

var result = await session.ExecAsync(Script);

Console.Write(result.Stdout.ToString());

if (!result.Stderr.IsEmpty)
{
    Console.Error.Write(result.Stderr.ToString());
}

// The whole vocabulary this session has, which is the point of the exercise: it is a list
// the host wrote, and `tar` is not on it.
Console.WriteLine();
Console.WriteLine($"== {session.BuiltinNames.Count} commands, of which the host added ==");
Console.WriteLine(string.Join(
    ", ",
    session.BuiltinNames.Where(name => name is "ticket" or "ticket-export" or "queue-size" or "python").Order()));

return result.ExitCode;
