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

Both vendored trees are read-only and have CI/CD workflows and generated blobs stripped.
They are the *specification*: when behaviour is ambiguous, the Rust source is the answer.

Both upstreams are MIT licensed, as is this repository. `LICENSE` / `NOTICE` files are
preserved under each vendored tree for attribution.

## Non-negotiable properties (these define "correct")

Bashkit's value is its security posture. The port must preserve all of it:

1. **No process spawning.** Never `Process.Start`, never `fork`/`exec`. Every command is a
   managed implementation. A "builtin" that shells out is a bug, not a shortcut.
2. **No ambient filesystem access.** All file I/O goes through `IFileSystem`. Never call
   `System.IO.File` / `Directory` outside the `RealFileSystem` backend.
3. **No ambient network access.** HTTP is denied unless an allowlist is configured.
4. **Deterministic resource limits.** Command count, loop iterations, function depth,
   output size, filesystem size, parser fuel, wall-clock timeout. Limits are enforced, not
   advisory.
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
| `lib.rs`                         | `Bash.cs`, `BashBuilder.cs` | Public facade |

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
src/
  Computerwelt.Emulation.Bash/          the shell library
  Computerwelt.Emulation.Python/        the Python library
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
  spec/                                     shell acceptance corpus (from bashkit)
  monty-spec/                               python acceptance corpus (from monty)
  monty-extensions/                         fixtures for what this port adds beyond monty
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
