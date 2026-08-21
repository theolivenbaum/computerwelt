<img src="logo.png" alt="Computerwelt" width="160" align="right" />

# Computerwelt

A sandboxed **shell and Python runtime for .NET 10** — a C# port of two Rust projects,
built to be embedded in host applications and handed to LLM agents as a tool:

- **[Bashkit](https://github.com/everruns/bashkit)** — an in-process bash interpreter with
  a virtual filesystem
- **[Monty](https://github.com/pydantic/monty)** (Pydantic) — a minimal, secure Python
  interpreter for running LLM-written code

They share one virtual filesystem, so `python script.py` inside a shell script runs in the
same sandbox as everything around it — with no CPython, no container and no process.

```csharp
var bash = Bash.CreateBuilder()
    .WithWorkingDirectory("/home/agent")
    .WithLimits(ExecutionLimits.Strict)
    .Build();

var result = await bash.ExecAsync("echo hello | tr a-z A-Z");
Console.WriteLine(result.Stdout);   // HELLO
```

```csharp
var python = new PythonRunner().Run("print(sum(x * x for x in range(5)))");
Console.WriteLine(python.Stdout);   // 30
```

Or both at once, over one filesystem:

```csharp
var bash = Bash.CreateBuilder().WithPython().Build();

await bash.ExecAsync("""
    echo '17 4 42' > /data.txt
    python -c "print(max(int(x) for x in open('/data.txt').read().split()))"
    """);
// 42
```

## Packages

| Package | Contents |
|---|---|
| `Computerwelt` | both halves, joined over one virtual filesystem |
| `Computerwelt.Emulation.Bash` | the shell on its own |
| `Computerwelt.Emulation.Python` | the Python interpreter on its own |

Each project's root namespace is its package name, so a type's namespace says which package
it ships in.

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
| `src/Computerwelt.Emulation.Bash/` | the shell library |
| `src/Computerwelt.Cli/` | a script runner and REPL over the whole product |
| `src/Computerwelt.Emulation.Python/` | the Python library |
| `src/Computerwelt/` | the two joined: `python` as a shell command over one filesystem |
| `src/Computerwelt.Emulation.Python.Cli/` | a Python-only runner |
| `tests/Computerwelt.Emulation.Python.Tests/` | Python unit tests |
| `tests/Computerwelt.Emulation.Python.SpecTests/` | Python conformance runner |
| `tests/Computerwelt.Tests/` | integration: both interpreters over one filesystem |
| `tests/Computerwelt.Emulation.Bash.Tests/` | shell unit tests |
| `tests/Computerwelt.Emulation.Bash.SpecTests/` | shell conformance runner |
| `tests/spec/` | 2,521 golden shell cases carried over from bashkit |
| `tests/monty-spec/` | 568 Python fixtures carried over from monty |
| `.reference/` | the vendored Rust sources, read-only, used as the specification |

## Building and testing

```bash
dotnet build
dotnet test
```

Both conformance suites are **ratchet-based**: `tests/spec/baseline.json` records how many
cases each shell file currently passes and `tests/monty-spec/baseline.json` records which
Python fixtures pass, and each suite fails on a regression. After making cases pass, raise
the baseline:

```bash
COMPUTERWELT_UPDATE_BASH_BASELINE=1 dotnet test tests/Computerwelt.Emulation.Bash.SpecTests
COMPUTERWELT_UPDATE_PYTHON_BASELINE=1 dotnet test tests/Computerwelt.Emulation.Python.SpecTests
```

Never lower a baseline to make a build green.

## Status

| | conformance | notes |
|---|---|---|
| shell | **2,521 / 2,521** | 73 commands implemented; 27 cases skipped by upstream directive |
| python | **557 / 558** | parser, bytecode compiler, VM, types, builtins, the stdlib subset, dunders |

The one Python fixture that does not pass asserts that a temporary's `id()` is handed to
the next object of the same shape — an artifact of upstream's slot-recycling heap. Object
identity here is the host runtime's, and an id is never recycled; the reasoning is in
[`todo.md`](todo.md).

The two halves share one virtual filesystem: `src/Computerwelt/` adds `python` as a shell
command whose `os`, `os.path` and `open` are backed by the shell's `IFileSystem`.

See [`todo.md`](todo.md) for the ledger and [`CLAUDE.md`](CLAUDE.md) for the architecture
and the invariants that define "correct".

## Licence

MIT, matching both upstreams. Attribution is preserved under each vendored tree.
