# Computerwelt — a sandboxed shell and Python runtime for .NET

## What this repository is

A ground-up **C# / .NET 10 port of two Rust projects**, delivered as one library:

| Upstream | Vendored at | What it gives us |
|---|---|---|
| [Bashkit](https://github.com/everruns/bashkit) | `.reference/bashkit/` | in-process sandboxed **bash** with a virtual filesystem |
| [Monty](https://github.com/pydantic/monty) (Pydantic) | `.reference/monty/` | a minimal, secure **Python** interpreter for running LLM-written code |

They fit together the way they do upstream: Bashkit already exposes Monty as its
`python` builtin, so porting both means `python script.py` works inside the same sandbox,
against the same virtual filesystem, under the same resource limits — with no CPython, no
container and no process.

Both vendored trees are read-only and have their CI/CD workflows, their build and
release automation and their generated blobs stripped — a `justfile`, a `Makefile` or a
cargo-deny config specifies how to build a Rust crate this repository does not build, and
the only pipeline here is the one under `.devops/`.
They are the *specification*: when behaviour is ambiguous, the Rust source is the answer.

Both upstreams are MIT licensed, as is this repository. `LICENSE` / `NOTICE` files are
preserved under each vendored tree for attribution.

## Non-negotiable properties (these define "correct")

Bashkit's value is its security posture. The port must preserve all of it:

1. **No process spawning.** Never `Process.Start`, never `fork`/`exec`. Every command is a
   managed implementation. A "builtin" that shells out is a bug, not a shortcut.
   The one exception is the optional `Computerwelt.Playwright` package, and it is an
   exception to *where* the rule applies rather than to the rule: the **host** launches a
   browser, in host code, before any script runs, and `p.chromium.launch()` inside the
   sandbox hands back a handle to it. A script still cannot start a process, and a launch
   option a script passes is refused by name rather than honoured. Nothing outside that
   package may start anything.
2. **No ambient filesystem access.** All file I/O goes through `IFileSystem`. Never call
   `System.IO.File` / `Directory` outside the `RealFileSystem` backend. A failure crossing
   into Python is translated at the boundary: a `ShellException` reaching a sandboxed
   program is a host exception leaving the sandbox, not an error the program can catch.
3. **No ambient network access.** HTTP is denied unless an allowlist is configured. A
   browser widens that surface rather than changing the rule, because a page fetches on its
   own behalf: `PlaywrightOptions.AllowedHosts` is empty by default and enforced as a
   request filter on every browser context, not only on the navigation a script typed.
4. **Deterministic resource limits.** Command count, loop iterations, function depth,
   output size, filesystem size, directory depth, parser fuel, wall-clock timeout. Limits
   are enforced, not advisory — and never silently: a cap that is reached raises, because
   a traversal that quietly stopped part-way reports a subset as though it were the whole.
   Depth is capped where paths are *created* (`FsLimits.MaxDepth`) as well as where they
   are *walked* (`ExecutionLimits.MaxDirectoryDepth`); the first is the containment, the
   second is the backstop for a host-supplied filesystem this sandbox did not build.
5. **Multi-tenant isolation.** Two `Bash` instances share no mutable state.
6. **POSIX path semantics everywhere.** Virtual paths are POSIX even when the host is
   Windows. This is why `VPath` exists and why `System.IO.Path` must not be used for
   virtual paths — `Path.Combine("/a", "C:\\b")` and friends are host-dependent and would
   punch a hole straight through the sandbox.

## Architecture

Mirrors upstream module-for-module so the trees can be diffed by eye. The two halves ship
as `Computerwelt.Emulation.Bash` and `Computerwelt.Emulation.Python`; each project's root
namespace is its assembly name, so a file's namespace names the package it lands in.

### Shell — `src/Computerwelt.Emulation.Bash/`

| Upstream (`crates/bashkit/src/`) | Port (`src/Computerwelt.Emulation.Bash/`) | Purpose |
|---|---|---|
| `parser/`                        | `Parsing/`                | Lexer, AST, recursive-descent parser, parse budget |
| `interpreter/`                   | `Interpreter/`            | Evaluation, expansion, arithmetic, globbing, redirection, shell state |
| `fs/`                            | `FileSystems/`            | `IFileSystem` + InMemory / Overlay / Mountable / ReadOnly / Real backends |
| `builtins/`                      | `Builtins/`               | ~160 command implementations |
| `limits.rs`                      | `Limits/`                 | Execution + session + memory budgets |
| `analysis.rs`                    | `Analysis/`               | Static script analysis for permission prompts |
| `snapshot/`                      | `Snapshot/`               | Serialize/restore shell + VFS state |
| `network/`                       | `Network/`                | Allowlist, HTTP transport abstraction |
| `tool.rs`, `tool_def.rs`         | `Tooling/`                | `BashTool` LLM tool contract |
| `builtins/extension.rs`, `command_resolver` | `Extensibility/` | Host commands: `IShellExtension`, `ICommandResolver`, `DelegateBuiltin`, `CommandTable` |
| `lib.rs`                         | `Bash.cs`, `BashBuilder.cs` | Public facade |

### Browser — `src/Computerwelt.Playwright/` (optional package)

No upstream to mirror: this maps [playwright-python](https://github.com/microsoft/playwright-python)'s
`sync_api` — the specification, in the same sense the Rust trees are — onto Microsoft's .NET
`Microsoft.Playwright` driver.

| Part | Purpose |
|---|---|
| `PlaywrightOptions`, `NavigationPolicy` | what the host decides: browsers, caps, timeouts, the hosts a page may reach |
| `PlaywrightSession` | the host-owned browser backend; the only thing here that starts a process |
| `PlaywrightBrowsers` | `playwright install`, wrapped as a method so a host need not shell out |
| `PlaywrightLibrary`, `PlaywrightExtensions` | the `playwright` / `playwright.sync_api` modules, and `WithPlaywright` at each configuration point |
| `Interop/` | the async→sync bridge, the keyword-argument reader, value conversion, error translation |
| `Api/` | one wrapper per Python class, answering attributes by name |

Two rules shape the whole package. Every path a script names is a **virtual** path — bytes
make the round trip through this process rather than the driver being handed a host path —
and every driver call is **translated**, so no .NET exception reaches a program.
`src/Computerwelt.Playwright/README.md` is the full mapping and the list of what is
deliberately absent.

### Python — `src/Computerwelt.Emulation.Python/`

Monty is a compiler plus a bytecode VM, not a tree walker, and the port keeps that shape:
the speed and the snapshot-at-a-call-boundary feature both depend on it.

| Upstream (`crates/monty/src/`) | Port (`src/Computerwelt.Emulation.Python/`) | Purpose |
|---|---|---|
| `parse.rs`, `fstring.rs`, `expressions.rs` | `Parsing/` | Python source → AST |
| `bytecode/` | `Compilation/` | AST → bytecode, scope and name resolution |
| `run.rs`, `function.rs`, `heap/` | `Runtime/` | the VM, frames, the object heap and its GC |
| `types/` | `Types/` | `int`, `str`, `list`, `dict`, `set`, `tuple`, `bytes`, … |
| `builtins/` | `Builtins/` | `len`, `range`, `print`, `sorted`, … |
| `modules/` | `Modules/` | the permitted stdlib subset |
| `crates/monty-types/` | `Interop/` | host-facing object model and external functions |
| — | `Extensibility/` | host libraries: `PythonLibrary`, built per run, written in C# or in Python |
| `crates/monty-fs/` | `Computerwelt/PythonFileSystem` | `os`, `os.path` and `open` over this repo's `IFileSystem`, so both sandboxes share one VFS |
| `crates/monty-type-checking/` | — | out of scope: wraps `ty`, an external type checker |

### Naming

Identifiers carry this product's names, not the upstreams': `ShellException`, `PythonRunner`,
`Computerwelt.Emulation.Bash`. Three strings are the exception, because they are behaviour
the acceptance corpora pin rather than branding — `bash --version`'s banner, `sys.platform`
(`monty`) and `sys.version` (`3.14.0 (Monty)`). Prose may still name an upstream when it is
citing it as the specification.

### Rust → C# idiom map

| Rust | C# |
|------|-----|
| `async fn` + tokio | `ValueTask<T>` / `Task<T>`, `CancellationToken` threaded explicitly |
| `Arc<dyn Trait>` | `interface` + DI by constructor; instances must be thread-safe |
| `enum` with payloads | `abstract record` + `sealed record` cases, matched with `switch` patterns |
| `Result<T, E>` | Exceptions for *fatal* errors (`ShellException`); `ExecResult` carries ordinary non-zero exits |
| `Option<T>` | Nullable reference types (`#nullable enable` is on and warnings are errors) |
| `&[u8]` / `Vec<u8>` | `ReadOnlySpan<byte>` / `byte[]`, wrapped by `StreamData` |
| `PathBuf` | `VPath` (POSIX-only readonly struct) |
| `clap` derive | Hand-written `ArgCursor` POSIX/GNU option parser (no external dep) |
| `#[cfg(feature = "x")]` | Optional assemblies / registration-time opt-in, not `#if` |

### Key invariants in the port

- **Bytes, not strings, are the unit of shell data.** `StreamData` holds bytes; UTF-8
  decoding is lossy and happens only at the edges. Do not round-trip binary through
  `string`.
- **Exit code 127** = command not found, **126** = not executable, **2** = parse/usage
  error. These leak into scripts; keep them exact.
- **`ExecResult` is a value**, produced and returned; builtins never write to a global
  stdout.
- Every builtin is **stateless and thread-safe** (`IBuiltin` implementations are shared
  singletons across executions); per-execution data lives in `BuiltinContext`.

## Layout

```
Computerwelt.slnx
Directory.Build.props        shared TFM / analyzers / warnings-as-errors
Directory.Packages.props     central package versions
.devops/build-nuget.yml      the pipeline that builds, tests, packs and publishes
src/
  Computerwelt.Emulation.Bash/          the shell library
  Computerwelt.Emulation.Python/        the Python library
  Computerwelt.Playwright/              optional: playwright's python sync_api over the .NET driver
  Computerwelt/                         the two joined: `python` as a shell command over one VFS
  Computerwelt.Cli/                     a REPL / script runner over the whole product
  Computerwelt.Emulation.Python.Cli/    a Python-only runner, for isolating that half
tests/
  Computerwelt.Emulation.Bash.Tests/        shell unit tests
  Computerwelt.Emulation.Bash.SpecTests/    shell conformance over `tests/spec/**/*.test.sh`
  Computerwelt.Emulation.Python.Tests/      python unit tests
  Computerwelt.Emulation.Python.SpecTests/  python conformance over `tests/monty-spec/*.py`
  Computerwelt.Tests/                       integration: both interpreters over one filesystem
  Computerwelt.AgentTests/                  the operations a caller performs, end to end
  Computerwelt.Playwright.Tests/            the navigation policy, and browser scenarios written as Python
  spec/                                     shell acceptance corpus (from bashkit)
  monty-spec/                               python acceptance corpus (from monty)
  monty-extensions/                         fixtures for what this port adds beyond monty
bench/
  Computerwelt.Benchmarks/                  upstream's benchmark corpus, and micro-benchmarks
samples/
  Computerwelt.Sample.Extensibility/        a runnable tour of every extension point
.reference/bashkit/          vendored bashkit source (read-only)
.reference/monty/            vendored monty source (read-only)
```

## The spec suites are the acceptance criteria

Both upstreams ship golden corpora that transfer to the port unchanged, and each is the
definition of "correct" for its half.

### Shell — `tests/spec/`

Bashkit ships **2,521 runnable cases** in a language-agnostic format:

```
### test_name
# optional description
<script lines>
### expect
<expected stdout>
### end
```

Directives: `### exit_code: N`, `### skip: reason`, `### bash_diff: reason`,
`### paused_time`.

`Computerwelt.Emulation.Bash.SpecTests` parses these and runs them. Because the port is incomplete, the runner
is **ratchet-based**: `tests/spec/baseline.json` records the pass count per file. A run
fails if any file regresses below its baseline. Raise the baseline when you make things
pass — never lower it to make a build green.

### Python — `tests/monty-spec/`

Monty ships **568 `.py` fixtures**, copied verbatim. They need no harness format at all:
each is ordinary Python whose body is `assert` statements, so a case passes when the file
runs to completion without raising.

```python
# === Simple interpolation ===
x = 'world'
assert f'hello {x}' == 'hello world'
```

Some fixtures instead pin an expected traceback in a trailing docstring
(`TRACEBACK:` …) or an expected exception on a comment (`# Raise=TypeError(...)`), which
makes them error-message conformance tests. `# xfail=monty` marks a case upstream itself
does not pass.

`Computerwelt.Emulation.Python.SpecTests` will run these under the same ratchet discipline.

### Python extensions — `tests/monty-extensions/`

Same fixture format, opposite purpose: every file here exercises something upstream Monty
does **not** have, so running one against upstream fails — usually at the import. Keeping
them in their own folder is what lets a reader tell a port decision from a specification,
and `COMPUTERWELT_SKIP_EXTENSIONS=1` switches the folder off so you can check the port
still stands on upstream's corpus alone. The suite is absolute, not ratcheted.

Nothing here may contradict `tests/monty-spec/`. Where the two would disagree, upstream
wins and the behaviour is recorded as a limitation in `todo.md` instead.

```bash
dotnet test                                   # everything
dotnet test tests/Computerwelt.Emulation.Bash.SpecTests           # shell conformance only
dotnet test tests/Computerwelt.Emulation.Python.SpecTests             # python conformance only
```

### Joined — `tests/Computerwelt.AgentTests/`

A third suite, and the one that catches what the other two structurally cannot. Both
corpora test features; this tests *operations* — the shapes a caller actually types, taken
from real sessions rather than invented: read a file, search a tree, patch a source file
with a heredoc Python program, check the result, keep going. Composing correct builtins is
where the interesting failures live, and every defect it has found so far was in code both
corpora already covered.

It also carries upstream's `python` command corpus (`spec/python.test.sh`), which the
shell suite cannot run — that command exists only once both halves are joined. Unlike the
two ratcheted suites it is absolute: every case must pass.

When adding to it, keep the discipline: an operation goes in because someone performed it,
not because it would round out a matrix.

## Measuring it

`bench/Computerwelt.Benchmarks/` carries bashkit's own benchmark corpus —
`crates/bashkit-bench/src/cases.rs`, all 96 cases, case for case — plus micro-benchmarks
for session construction, parsing, the filesystem and the Python half. Two ways in:

```bash
dotnet run -c Release --project bench/Computerwelt.Benchmarks -- --report   # stopwatch, seconds
dotnet run -c Release --project bench/Computerwelt.Benchmarks -- --filter '*Session*'  # BenchmarkDotNet
```

`--report` is for the edit loop: µs/op and bytes/op per case, and it checks each case
against the output upstream recorded, because a case that stopped producing the right
answer is not a faster case. BenchmarkDotNet is for before you believe a number. Nothing
here is a ratchet — these are measurements, not acceptance criteria, and `todo.md` records
what the last round of them changed and what they still point at.

## Building and publishing

```bash
dotnet build
dotnet test
```

Both work with no arguments from the repository root, and keeping it that way is the point:
`Computerwelt.slnx` is the only solution, every project in it builds from a clean clone, and
the suites are xunit on the standard test SDK, so nothing needs a runner selection or a
`global.json`. The whole suite is a couple of minutes, most of it in the two conformance
corpora.

`.devops/build-nuget.yml` is an Azure DevOps pipeline on a push to `main`, and it is the
only thing that publishes — there is no other build definition, so nothing releases by
accident. It restores, builds, runs the whole suite, packs and pushes the three library
packages (`Computerwelt`, `Computerwelt.Emulation.Bash`, `Computerwelt.Emulation.Python`)
plus the optional `Computerwelt.Playwright`; the two CLIs and the seven test projects opt
out with `IsPackable`.

The version is CalVer computed in the pipeline — `yy.M.<build id mod 65536>`, the scheme the
other packages from this organisation use — so no release version is committed anywhere and
a local build simply gets the SDK's default; the pipeline's `/p:Version` overrides it. The
push goes through the `nuget-curiosity-org` service connection, so no API key lives in this
repository.

The tests are the release gate, and both ratchets carry their weight there:
`tests/spec/baseline.json` fails the run if any spec file regresses, and
`Computerwelt.AgentTests` is absolute. A red suite is a package that never gets built, which
is the only ordering that helps — a package pushed to NuGet cannot be un-published.

## Extending the sandbox

A host adds vocabulary; it never adds authority. Everything registered runs under the same
limits, against the same virtual filesystem, with no route to the host that the sandbox did
not already have.

| Point | Shape | Notes |
|---|---|---|
| `BashBuilder.WithBuiltin` | `IBuiltin`, or a delegate | replaces a default of the same name |
| `BashBuilder.WithExtension` | `IShellExtension` | a set of commands granted as one unit |
| `BashBuilder.WithCommandResolver` | `ICommandResolver` | consulted **last**, so it can never shadow a function, a command or a script; its names are not enumerable |
| `BashBuilder.WithoutBuiltin` | a name | applied after every registration, so it loses to nothing; the name is absent, not refusing |
| `PythonRunner.Libraries` / `PythonOptions.Libraries` | `PythonLibrary` | built **per run**, on first import; written in C# (`FromFactory`, `FromFunctions`) or in Python (`FromSource`) |
| `PythonRunner.HostFunctions` / `PythonOptions.HostFunctions` | `Func<PythonHostContext, PyObject[], PyObject>` | a C# function in the program's globals, handed the run's environment |
| `PythonRunner.ExternalFunctions` | `Func<PyObject[], PyObject>` | the same, for host code that needs no environment |
| `PythonRunner.Modules` | a `PyObject` | one object shared by every run — for a module with no state |
| `BashBuilder.WithPlaywright` / `PythonOptions.WithPlaywright` | a `PlaywrightSession` | the optional browser package: registers `playwright` and `playwright.sync_api` over a host-launched browser |

Host code written in C# is handed the environment rather than reaching for one:
`BuiltinContext` on the shell side, `PythonHostContext` on the Python side. Both carry the
virtual filesystem, the working directory, the environment and the run's limits and clock,
and both read them live — a `cd` the script performed is where host code finds itself. The
Python context translates the absence of storage into a Python `OSError`
(`RequireFileSystem`), because a host exception reaching a sandboxed program is the sandbox
leaking, not an error the program can handle.

There is deliberately no way to *compile* C# from inside a script. Host code is registered
by the host, in the host's own assembly, before the session is built — which is what keeps
the reachable surface a list somebody wrote.

Two invariants show up here, and both are load-bearing:

- A builtin instance is shared by every execution of every session it was registered with,
  so it must be stateless and thread-safe. Per-invocation data arrives in `BuiltinContext`.
- A library is a recipe, not a module. `PythonRunner.Modules` hands one object to every run,
  which is fine for a constant table and wrong for anything a program can mutate: that would
  be one tenant's state becoming another's. `Libraries` builds a fresh module per run, and a
  library written in Python has module-level state by construction — which is why it is the
  route those get added by.

## Working rules

- **Read the Rust before writing the C#.** Upstream files carry the edge cases in
  comments and inline `#[cfg(test)]` blocks; those tests are free specification.
  `.reference/monty/limitations/` additionally documents, per feature, exactly which
  Python semantics Monty does and does not implement — port the documented subset, not
  CPython.
- **Port behaviour, not structure-for-its-own-sake.** Idiomatic C# is preferred where it
  does not change observable behaviour. Where it would, match Rust exactly.
- **Every builtin lands with spec coverage.** If upstream has no spec case for a
  behaviour, add a unit test in `Computerwelt.Emulation.Bash.Tests` or `Computerwelt.Emulation.Python.Tests`.
- **Never weaken a limit or a check to make a test pass.**
- Keep `todo.md` current — it is the port's progress ledger.
