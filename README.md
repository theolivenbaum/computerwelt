# Computerwelt

A sandboxed **shell and Python runtime for .NET 10** — a C# port of two Rust projects,
built to be embedded in host applications and handed to LLM agents as a tool:

- **[Bashkit](https://github.com/everruns/bashkit)** — an in-process bash interpreter with
  a virtual filesystem
- **[Monty](https://github.com/pydantic/monty)** (Pydantic) — a minimal, secure Python
  interpreter for running LLM-written code

They share one virtual filesystem and one resource budget, so `python script.py` inside a
shell script runs in the same sandbox as everything around it — with no CPython, no
container and no process.

```csharp
var bash = Bash.CreateBuilder()
    .WithWorkingDirectory("/home/agent")
    .WithLimits(ExecutionLimits.Strict)
    .Build();

var result = await bash.ExecAsync("echo hello | tr a-z A-Z");
Console.WriteLine(result.Stdout);   // HELLO
```

```csharp
var python = new MontyRunner().Run("print(sum(x * x for x in range(5)))");
Console.WriteLine(python.Stdout);   // 30
```

## What "sandboxed" means here

- **No process spawning.** Every command is a managed implementation. There is no `PATH`
  lookup, no `fork`, no `exec`.
- **No ambient filesystem.** All I/O goes through `IFileSystem`; the default backend is an
  empty in-memory tree with a byte and file-count quota.
- **No ambient network.** HTTP is denied unless a host configures an allowlist.
- **Enforced limits.** Command count, loop iterations, recursion depth, output size,
  parser fuel and wall-clock time are all charged during evaluation.
- **Isolated.** Two `Bash` instances share nothing mutable.

Virtual paths are POSIX on every host — `VPath`, not `System.IO.Path` — so the sandbox
behaves identically on Linux, macOS and Windows.

## Layout

| Path | What it is |
|---|---|
| `src/Bashkit/` | the shell library |
| `src/Bashkit.Cli/` | a script runner and REPL over it |
| `src/Monty/` | the Python library |
| `src/Monty.Cli/` | a script runner over it |
| `tests/Monty.SpecTests/` | Python conformance runner |
| `tests/Bashkit.Tests/` | shell unit tests |
| `tests/Bashkit.SpecTests/` | shell conformance runner |
| `tests/spec/` | 2,521 golden shell cases carried over from bashkit |
| `tests/monty-spec/` | 568 Python fixtures carried over from monty |
| `.reference/` | the vendored Rust sources, read-only, used as the specification |

## Building and testing

```bash
dotnet build
dotnet test
```

The conformance suite is **ratchet-based**: `tests/spec/baseline.json` records how many
cases each file currently passes, and the suite fails on a regression. After making cases
pass, raise the baseline:

```bash
BASHKIT_UPDATE_BASELINE=1 dotnet test tests/Bashkit.SpecTests
```

Never lower a baseline number to make a build green.

## Status

| | conformance | notes |
|---|---|---|
| shell | **1,694 / 2,521** | 73 commands implemented |
| python | **371 / 558** | parser, bytecode compiler, VM, types, builtins, 9 stdlib modules, dunders |

The two halves do not yet share a filesystem; that is the last step of the Python port.

See [`todo.md`](todo.md) for the ledger and [`CLAUDE.md`](CLAUDE.md) for the architecture
and the invariants that define "correct".

## Licence

MIT, matching both upstreams. Attribution is preserved under each vendored tree.
