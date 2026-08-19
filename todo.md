# Bashkit → C# port: task ledger

Status legend: `[ ]` not started · `[~]` in progress / partial · `[x]` done

Upstream reference: `.reference/bashkit/crates/bashkit/src/`
Acceptance suite: `tests/spec/` (2,521 runnable cases after dropping the out-of-scope
`python` and `typescript` suites). Ratchet file: `tests/spec/baseline.json`.

**Current state:** solution builds clean, 151 unit tests green,
**1,578 / 2,521 conformance cases passing (62.6 %)**.

| suite | passing |
|---|---|
| `bash` | 1,440 / 2,094 |
| `grep` | 70 / 95 |
| `sed` | 66 / 80 |
| `jq` | 2 / 124 |
| `awk` | 0 / 126 |
| `yq` | 0 / 26 |

---

## Phase 0 — Groundwork

- [x] Vendor upstream into `.reference/bashkit/` (CI/CD + eval result blobs stripped)
- [x] `CLAUDE.md` — architecture, invariants, Rust→C# idiom map
- [x] `todo.md` — this ledger
- [x] Solution, `Directory.Build.props`, `Directory.Packages.props` (net10.0, nullable,
      warnings-as-errors, central package management)
- [x] Copy upstream spec cases into `tests/spec/`
- [x] `Bashkit.SpecTests` harness: spec file parser + ratchet runner + `report` mode

## Phase 1 — Core primitives  (`src/Bashkit/Core/`)

Upstream: `stream.rs`, `error.rs`, `interpreter/state.rs`, `fs/posix.rs`

- [x] `VPath` — POSIX-only virtual path value type (normalize, join, parent, filename,
      extension, absolute/relative, `..` resolution without touching the host FS)
- [x] `StreamData` — byte-oriented stdio payload with lossy UTF-8 view
- [x] `ExecResult` — stdout/stderr/exit code/control flow/truncation flags
- [x] `ControlFlow` — none / break(n) / continue(n) / return(code) / exit(code)
- [x] `BashkitException` + error taxonomy (`Error` variants from `error.rs`)
- [x] `ExitCodes` constants (127 not-found, 126 not-executable, 2 usage, 128+n signals)

## Phase 2 — Limits  (`src/Bashkit/Limits/`)

Upstream: `limits.rs` (73 KB), `profile.rs`

- [x] `ExecutionLimits` — commands, loop iterations, total loop iterations, function
      depth, timeout, parser timeout, input size, output size, work units
- [x] `ExecutionBudget` — shared counters for commands, loop iterations, work units,
      parser fuel, function/nesting depth, wall-clock deadline and cancellation
- [ ] `SessionLimits` + `ExecutionCounters` — cross-exec accounting
- [ ] `MemoryLimits` / `MemoryBudget` — live intermediate byte tracking with leases
- [ ] `ExecutionProfile` presets (strict / default / permissive)

## Phase 3 — Virtual filesystem  (`src/Bashkit/FileSystems/`)

Upstream: `fs/` (~400 KB)

- [x] `IFileSystem` — async read/write/append/mkdir/remove/stat/readdir/exists/rename/
      copy/symlink/readlink/chmod/set-mtime/usage/limits
- [x] `Metadata`, `DirEntry`, `FileType`, `FsUsage`, `FsLimits`, `FsError` factories
- [x] `InMemoryFileSystem` — full backend (files, dirs, symlinks, modes, mtimes, quota)
- [ ] Symlink loop detection + `O_NOFOLLOW`-equivalent resolution rules
- [ ] `OverlayFileSystem` — copy-on-write lower/upper with whiteouts
- [ ] `MountableFileSystem` — mount table, longest-prefix routing
- [ ] `ReadOnlyFileSystem` wrapper
- [ ] `RealFileSystem` — opt-in host backend with jail root + escape checks
- [ ] `ISearchCapable` fast-path for `grep`/`rg`/`find`
- [ ] Namespace / `posix.rs` path-resolution conformance suite
      (`tests/support/filesystem_security_conformance.rs` upstream)

## Phase 4 — Parser  (`src/Bashkit/Parsing/`)

Upstream: `parser/` (~340 KB)

- [x] `Token` + `TokenKind` (words, literal/quoted/quoted-glob words, operators,
      redirect ops, heredoc ops, keywords)
- [x] `Lexer` — quoting state machine, escapes, `$`-forms, backticks, arithmetic `$((`,
      command subst `$(`, process subst `<(`/`>(`, heredoc collection, line continuation,
      comments, operator maximal-munch
- [x] AST records — `Script`, `Command`, `SimpleCommand`, `Pipeline`, `CommandList`,
      `CompoundCommand` (+ if/for/select/arith-for/while/until/case/subshell/group/
      `[[ ]]`/`(( ))`/coproc/time), `FunctionDef`, `Word`, `WordPart`, `ParameterOp`,
      `Redirect`, `Assignment`
- [x] `Parser` — recursive descent over the token stream, fuel budgeted
- [x] Heredoc `<<-` tab stripping + quoted-delimiter (no-expansion) semantics
- [ ] `select`, `coproc`, `time`, process substitution parsing
- [ ] Parse-error messages and exit code 2 parity (`parse-errors.test.sh`)
- [~] Parse fuel wired; parser wall-clock timeout not yet separate from execution timeout
- [~] `Span` on nodes; not yet complete or used by analysis

## Phase 5 — Interpreter  (`src/Bashkit/Interpreter/`)

Upstream: `interpreter/` (~730 KB — the largest single area)

- [x] `ShellState` — variables (scalar/indexed/associative), attributes (export/readonly/
      integer/nameref), functions, aliases, options (`set -e/-u/-x/-o pipefail`, `shopt`),
      positional params, special params (`$?`, `$$`, `$!`, `$#`, `$@`, `$*`, `$_`)
- [x] Scope stack with `local` and dynamic scoping semantics
- [x] `Interpreter` — command lists, `&&`/`||`, `;`, pipelines, subshells, groups
- [x] Control flow — `if`, `while`, `until`, `for`, C-style `for`, `case`
- [x] Function definition + invocation, call depth limits, `return`
- [x] Word expansion pipeline in correct order: brace → tilde → parameter → command
      subst → arithmetic → word split → glob → quote removal
- [x] Parameter expansion operators — `:-` `:=` `:?` `:+` `#` `##` `%` `%%` `/` `//`
      `^` `^^` `,` `,,` `:off:len` `!prefix*` `!name[@]` `@Q/E/P/A/a/U/L`
- [x] Arithmetic evaluator — full C precedence, `**`, ternary, comma, assignment ops,
      pre/post inc/dec, bases (`0x`, `0`, `N#ddd`), variable auto-resolution
- [x] Redirections — `>` `>>` `<` `<<` `<<<` `<>` `>&` `<&` `&>` `&>>`, fd dup/close,
      per-command scoping
- [x] Pipelines with `pipefail` + `PIPESTATUS`
- [x] Globbing — `*`, `?`, `[...]`, character classes, `**` under `globstar`,
      `extglob` (`?()` `*()` `+()` `@()` `!()`), dotfile rules, `nullglob`/`failglob`
- [x] Brace expansion — lists, numeric/alpha ranges with increment, nesting
- [ ] Command substitution trailing-newline stripping + nested quoting edge cases
- [x] `[[ ]]` conditional expressions incl. `=~` regex + `BASH_REMATCH`
- [~] Arrays: indexed + associative + splat + append done; slicing and `${!arr[@]}` not
- [~] `trap` records handlers; firing them on `EXIT`/`ERR`/`DEBUG`/`RETURN` is not done
- [ ] Job control simulation (`&`, `jobs`, `wait`, `%1`)
- [~] `set -e` fires and is suppressed after `&&`/`||`/`!`; the full context list is unverified
- [~] `set -x` emits `+ cmd` to stderr; `PS4` and structured `TraceEvent` not yet
- [ ] Process substitution execution
- [ ] `time` keyword, `coproc`
- [ ] `IFS`-driven word splitting edge cases (upstream has 10 known-failing cases here)

## Phase 6 — Builtins  (`src/Bashkit/Builtins/`)

~160 commands. `IBuiltin`, `BuiltinContext` and the `ArgCursor` option parser are in
place, along with `ShellHooks` for the builtins that call back into the shell
(`eval`, `source`, `command`, `xargs`, `find -exec`). 71 commands are registered.

### Shell builtins
- [x] `echo` `printf` `true` `false` `:` `exit` `cd` `pwd` `test` `[`
- [x] `export` `unset` `set` `shift` `local` `readonly` `read` `shopt`
- [x] `declare`/`typeset` `alias` `unalias` `type`
- [x] `eval` `source`/`.` `trap` `which` `hash` `command` `getopts` `let`
- [ ] `mapfile`/`readarray` `caller` `times` `wait` `kill`
- [ ] `compgen` `fc` `history` `help` `ulimit` `umask`
- [~] `trap` records handlers; they are not yet fired on EXIT/ERR/DEBUG

### File & directory
- [x] `cat` `ls` `mkdir` `rm` `cp` `mv` `touch` `basename` `dirname`
- [x] `find` `rmdir` `ln` `chmod` `stat` `truncate` `mktemp` `realpath` `readlink`
      `pushd` `popd` `dirs`
- [ ] `tree` `chown` `file` `mkfifo` `less` `du` `df`

### Text processing
- [x] `head` `tail` `wc` `sort` `uniq` `rev` `tac` `seq` `yes`
- [x] `grep` (+ `egrep`, `fgrep`) `sed` `cut` `tr` `nl` `paste`
- [ ] `awk` `column` `comm` `diff` `patch` `join` `split` `fold` `expand` `unexpand`
      `strings` `shuf` `csv` `template` `envsubst` `iconv`
- [ ] `rg` (ripgrep subset)

### Data formats
- [ ] `jq` (full JSON query language — large sub-project) `yq` `tomlq` `json`
- [ ] `base64` `md5sum` `sha1sum` `sha256sum` `od` `xxd` `hexdump`

### Archives
- [ ] `tar` `gzip` `gunzip` `bzip2` `bunzip2` `bzcat` `zip` `unzip`

### Math / misc
- [x] `expr` `env` `printenv`
- [x] `sleep` `xargs` `tee` `id` `whoami` `hostname` `uname`
- [ ] `bc` `numfmt` `semver` `timeout` `retry` `watch` `parallel` `date`
- [ ] `clear` `assert` `verify` `dotenv` `glob` `log`

### Network (allowlist-gated)
- [ ] `curl` `wget` `http`

### Optional / feature-gated
- [ ] `git` (virtual git over the VFS) — upstream marks experimental
- [ ] `ssh` / `scp` / `sftp`
- [ ] `python`, `typescript`, `sqlite` embeddings — **out of scope**; upstream delegates
      to third-party Rust engines with no .NET equivalent. Track as "not ported".

## Phase 7 — Public facade  (`src/Bashkit/`)

Upstream: `lib.rs`, `tool.rs`, `tool_def.rs`, `tool_registry.rs`

- [x] `Bash` — `ExecAsync`, cwd/env accessors, `FileSystem` property
- [x] `BashBuilder` — fs, env, cwd, limits, username/hostname, custom builtins
- [~] `ExecOptions` — arg0, positional params, stdin done; streaming callback not
- [ ] Streaming output callbacks + `IAsyncEnumerable<StreamChunk>`
- [ ] `ExecutionHandle` / host-call protocol (`host_call.rs`)
- [ ] `Hooks` (pre/post command interception)
- [ ] Mounts (`HostMount`, `HostMounts`, `Mount`/`Unmount`)
- [ ] `Credential` injection + redaction
- [ ] `ITool` / `BashTool` LLM contract: description, system prompt, discovery metadata
- [ ] `ToolRegistry`, `ToolDef`, scripted-tool orchestration

## Phase 8 — Analysis, snapshot, network, tracing

- [ ] `ScriptAnalysis` — commands, args, file writes, network reach, *before* execution
- [ ] `Snapshot` — shell state + VFS content graph, chunking, restore
- [ ] `NetworkAllowlist` — per-domain rules, default deny
- [ ] `IHttpTransport` abstraction + `HttpLimits`
- [ ] `TraceEvent` / `TraceMode` structured tracing
- [ ] `LogConfig` + secret redaction

## Phase 9 — Hosting surfaces

- [x] `Bashkit.Cli` — run a script, `-c`, REPL
- [ ] NuGet packaging metadata, symbols, deterministic build
- [ ] Public API surface tests (`PublicAPI.Shipped.txt`)
- [ ] Benchmarks (`BenchmarkDotNet`) mirroring `crates/bashkit-bench`

## Phase 10 — Hardening

- [ ] Port the 280+ threat-model mitigations (`.reference/bashkit/knowledge/security/`)
- [ ] Property tests for parser/expansion (`proptest_security.rs` equivalent, FsCheck)
- [ ] Fuzz targets for lexer/parser/arithmetic (`fuzz/`)
- [ ] Fill in the ratchet to 100 % of non-skipped spec cases

---

## Deliberately out of scope

| Upstream area | Reason |
|---|---|
| `python` / `typescript` / `sqlite` builtins | Wrap third-party Rust engines (Monty, ZapCode, Turso) with no .NET counterpart |
| `bashkit-capi`, `bashkit-wasm`, `bashkit-js`, `bashkit-python` | FFI/binding crates; .NET consumers use the library directly |
| `bashkit-eval` | LLM benchmark harness, not runtime behaviour |
| `bashkit-coreutils-port` | Rust-specific codegen tooling |
