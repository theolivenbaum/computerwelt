# Extending Computerwelt

A support-desk sandbox, in about a hundred lines: the shell gets a domain vocabulary,
Python gets two libraries, and three commands are taken away.

```bash
dotnet run --project samples/Computerwelt.Sample.Extensibility
```

## What it demonstrates

| File | Extension point | Why that one |
|---|---|---|
| `TicketsExtension.cs` | `IShellExtension` → `WithExtension` | A related set of commands granted as one unit, so the capability can be read — and withheld — in one line |
| `Program.cs` | `WithBuiltin(name, delegate)` | One command that does not need a class |
| `RunbookResolver.cs` | `ICommandResolver` → `WithCommandResolver` | A name space too large to enumerate, answered one name at a time |
| `Program.cs` | `WithoutBuiltins("tar", "curl", "wget")` | A deny-list that leaves the names genuinely absent |
| `PythonLibraries.Tickets()` | `PythonLibrary.FromFactory` | A Python library written in C#, because it reads the sandbox's filesystem |
| `PythonLibraries.Formatting()` | `PythonLibrary.FromSource` | A Python library written in Python, because nothing about it needs C# |
| `PythonLibraries.HostFunctions()` | `PythonOptions.HostFunctions` | C# functions any Python program can call without importing anything, each handed the live environment |

The point of running the two halves together is the middle of the output: `ticket` writes
the queue into the session's virtual filesystem, the `tickets` Python library reads that
same file, and `cat` reads the report Python wrote back. One filesystem, no host access
anywhere in it.

## Implementing functionality in C#

Three pieces of this sample are C# doing real work inside the sandbox, and each one is
handed the environment rather than reaching for one:

- `TicketsExtension` — a shell command. Its `BuiltinContext` carries the filesystem, the
  arguments, the exported environment and standard input.
- `PythonLibraries.Tickets()` — a Python module. Its `PythonHostContext` carries the same
  filesystem, which over a joined session is the shell's, so it reads the file `ticket`
  wrote.
- `PythonLibraries.HostFunctions()` — `whereami` and `disk_usage`, callable from any Python
  program with no import at all.

Both contexts read the environment live, which is why `whereami` reports `/srv` after the
script has `cd`-ed there and `/` after it has come back. Where storage might be absent,
`RequireFileSystem()` raises a Python `OSError` the program can catch — a host exception
escaping into a sandboxed program would be the sandbox leaking.

## The rules the sample is written to

- **Builtins are shared and must be stateless.** One `TicketsExtension` instance serves
  every session it is registered with; the tickets live in each session's own filesystem,
  which is why two sessions sharing the extension still see two separate queues.
- **A resolver cannot shadow anything.** It is consulted after shell functions, registered
  commands and the search for a script — so `runbook:vpn` resolves and `echo` never does.
  Its names are not enumerable, which is the honest report: the host cannot list them either.
- **A library is built per run.** Each `python` invocation gets a fresh `formatting` and a
  fresh `tickets`; module-level state cannot ride from one command to the next, or from one
  tenant to another.
- **A withheld name is absent, not refused.** `tar` exits 127 and does not appear in
  `type`, `command -v` or `Bash.BuiltinNames`, so a script cannot discover a capability it
  does not have.
