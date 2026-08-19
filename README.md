# Computerwelt

A C# / .NET 10 port of [Bashkit](https://github.com/everruns/bashkit) — an in-process,
sandboxed bash interpreter with a virtual filesystem, built to be embedded in host
applications and handed to LLM agents as a tool.

```csharp
var bash = Bash.CreateBuilder()
    .WithWorkingDirectory("/home/agent")
    .WithLimits(ExecutionLimits.Strict)
    .Build();

var result = await bash.ExecAsync("echo hello | tr a-z A-Z");
Console.WriteLine(result.Stdout);   // HELLO
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
| `src/Bashkit/` | the library |
| `src/Bashkit.Cli/` | a script runner and REPL over it |
| `tests/Bashkit.Tests/` | unit tests |
| `tests/Bashkit.SpecTests/` | conformance runner |
| `tests/spec/` | 2,521 golden cases carried over from upstream |
| `.reference/bashkit/` | the vendored Rust source, read-only, used as the specification |

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

The port is in progress. See [`todo.md`](todo.md) for the ledger and
[`CLAUDE.md`](CLAUDE.md) for the architecture and the invariants that define "correct".

## Licence

MIT, matching upstream. Attribution is preserved in `.reference/bashkit/NOTICE`.
