# Computerwelt — C# port of Bashkit

## What this repository is

A ground-up **C# / .NET 10 port of [Bashkit](https://github.com/everruns/bashkit)** — an
in-process, sandboxed bash interpreter with a virtual filesystem, designed to be embedded
in host applications (notably as an LLM tool).

The upstream Rust source is vendored read-only under **`.reference/bashkit/`** (CI/CD
workflows and generated eval result blobs stripped). It is the *specification*: when
behaviour is ambiguous, the Rust source is the answer.

Upstream is MIT licensed; so is this repository. `.reference/bashkit/NOTICE` and
`.reference/bashkit/LICENSE` are preserved for attribution.

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

Mirrors upstream module-for-module so the two trees can be diffed by eye.

| Upstream (`crates/bashkit/src/`) | Port (`src/Bashkit/`)     | Purpose |
|----------------------------------|---------------------------|---------|
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

### Rust → C# idiom map

| Rust | C# |
|------|-----|
| `async fn` + tokio | `ValueTask<T>` / `Task<T>`, `CancellationToken` threaded explicitly |
| `Arc<dyn Trait>` | `interface` + DI by constructor; instances must be thread-safe |
| `enum` with payloads | `abstract record` + `sealed record` cases, matched with `switch` patterns |
| `Result<T, E>` | Exceptions for *fatal* errors (`BashkitException`); `ExecResult` carries ordinary non-zero exits |
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
Computerwelt.sln
Directory.Build.props        shared TFM / analyzers / warnings-as-errors
Directory.Packages.props     central package versions
src/
  Bashkit/                   the library
  Bashkit.Cli/               a REPL / script runner over the library
tests/
  Bashkit.Tests/             unit tests (xUnit v3)
  Bashkit.SpecTests/         conformance runner over `tests/spec/**/*.test.sh`
  spec/                      spec cases (copied from upstream; the acceptance suite)
.reference/bashkit/          vendored upstream Rust source (read-only)
```

## The spec suite is the acceptance criterion

Upstream ships **2,650 golden test cases** in a language-agnostic format, copied to
`tests/spec/`:

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

`Bashkit.SpecTests` parses these and runs them. Because the port is incomplete, the runner
is **ratchet-based**: `tests/spec/baseline.json` records the pass count per file. A run
fails if any file regresses below its baseline. Raise the baseline when you make things
pass — never lower it to make a build green.

```bash
dotnet test                                   # everything
dotnet test tests/Bashkit.SpecTests           # conformance only
dotnet run --project tests/Bashkit.SpecTests -- report   # pass-rate breakdown
```

## Working rules

- **Read the Rust before writing the C#.** Upstream files carry the edge cases in
  comments and inline `#[cfg(test)]` blocks; those tests are free specification.
- **Port behaviour, not structure-for-its-own-sake.** Idiomatic C# is preferred where it
  does not change observable behaviour. Where it would, match Rust exactly.
- **Every builtin lands with spec coverage.** If upstream has no spec case for a
  behaviour, add a unit test in `Bashkit.Tests`.
- **Never weaken a limit or a check to make a test pass.**
- Keep `todo.md` current — it is the port's progress ledger.
