# Computerwelt port: task ledger

Two Rust upstreams are being ported into one .NET library:

| Part | Upstream | Reference | Acceptance corpus |
|---|---|---|---|
| shell | [bashkit](https://github.com/everruns/bashkit) | `.reference/bashkit/` | `tests/spec/` (2,521 cases) |
| python | [monty](https://github.com/pydantic/monty) | `.reference/monty/` | `tests/monty-spec/` (568 fixtures) |

Phases 0–10 below cover the shell. Phases 11–17 cover the Python interpreter.

Status legend: `[ ]` not started · `[~]` in progress / partial · `[x]` done

Upstream reference: `.reference/bashkit/crates/bashkit/src/`
Acceptance suite: `tests/spec/` (2,521 runnable cases after dropping the out-of-scope
`python` and `typescript` suites). Ratchet file: `tests/spec/baseline.json`.

**Current state — shell:** solution builds clean, 151 unit tests green,
**1,694 / 2,521 conformance cases passing (67.2 %)**.

**Current state — python:** tokenizer, parser, bytecode compiler, VM, core types,
builtins, nine stdlib modules, dunder dispatch and the external-function boundary are in
place. **371 / 558 fixtures passing (66.5 %)**. No host exception escapes to a script any
more; every remaining failure is Python-level.

Largest remaining gaps: ~75 real semantic differences, 22 fixtures needing `asyncio`,
`os`/`pathlib` or `unicodedata`, and 20 pinning tracebacks the port does not format yet.

| suite | passing |
|---|---|
| `bash` | 1,556 / 2,094 |
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
- [x] Arrays: indexed, associative, splat, append, slicing, `${!arr[@]}`, `${!prefix*}`
- [~] `trap`: `EXIT` and `ERR` fire; `DEBUG` and `RETURN` do not
- [ ] Job control simulation (`&`, `jobs`, `wait`, `%1`)
- [~] `set -e` fires and is suppressed after `&&`/`||`/`!`; the full context list is unverified
- [~] `set -x` emits `+ cmd` to stderr; `PS4` and structured `TraceEvent` not yet
- [ ] Process substitution execution
- [ ] `time` keyword, `coproc`
- [x] `IFS` field splitting with the whitespace / non-whitespace separator distinction

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
- [x] `date` `timeout`
- [ ] `bc` `numfmt` `semver` `retry` `watch` `parallel`
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

- [x] `Computerwelt.Cli` — run a script, `-c`, REPL, with `python` available
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
| bashkit `typescript` / `sqlite` builtins | Wrap third-party Rust engines (ZapCode, Turso) with no .NET counterpart |
| `bashkit-capi`, `bashkit-wasm`, `bashkit-js`, `bashkit-python` | FFI/binding crates; .NET consumers use the library directly |
| `bashkit-eval` | LLM benchmark harness, not runtime behaviour |
| `bashkit-coreutils-port` | Rust-specific codegen tooling |
| `monty-type-checking` | Wraps [`ty`](https://docs.astral.sh/ty/), an external type checker |
| `monty-js`, `monty-python`, `monty-wasm-runtime`, `monty-proto` | Binding and transport crates |
| `monty-typeshed` | Type stubs, only needed by the type checker |
| CPython compatibility beyond Monty's documented subset | Monty deliberately omits inheritance, metaclasses, `match`, third-party packages and most of the stdlib. `.reference/monty/limitations/` is the contract; the port matches Monty, not CPython |

---

# Python interpreter (Monty port)

Upstream reference: `.reference/monty/crates/monty/src/` (~139k lines of Rust).
Acceptance suite: `tests/monty-spec/` — 568 `.py` fixtures whose bodies are `assert`
statements, so a case passes when it runs to completion without raising. Fixtures with a
trailing `TRACEBACK:` docstring or a `# Raise=` comment additionally pin the error
message. `# xfail=monty` marks fixtures upstream itself does not pass.

Monty compiles to bytecode and runs a VM. The port keeps that architecture: both the
startup speed and the snapshot-at-a-call-boundary feature depend on it, and a tree-walking
shortcut would foreclose them.

## Phase 11 — Groundwork

- [x] Vendor upstream into `.reference/monty/` (CI/CD stripped)
- [x] Copy the 568-fixture corpus into `tests/monty-spec/`
- [x] `src/Monty/`, `src/Monty.Cli/` and `tests/Monty.SpecTests/`
- [x] Fixture parser: `assert`-only cases, `# Raise=`, `TRACEBACK:`, `# xfail=` directives
- [x] Ratchet runner + `tests/monty-spec/baseline.json`

## Phase 12 — Front end  (`src/Monty/Parsing/`)

Upstream: `parse.rs`, `expressions.rs`, `fstring.rs`, `source_map.rs`

- [x] Tokenizer: significant indentation, implicit line joining, string prefixes
      (`r`, `b`, `f`, `rb`), numeric literals
- [x] Expression grammar with Python's precedence, comparison chaining, walrus,
      conditional expressions, lambdas, starred and keyword arguments
- [x] Statement grammar: assignment and augmented assignment, `if`/`elif`/`else`,
      `while`, `for`/`else`, `try`/`except`/`else`/`finally`, `with`, `def`, `class`,
      `import`, `global`/`nonlocal`, `assert`, `del`, `raise`, `return`, `yield`
- [x] Comprehensions (list, set, dict, generator) with their own scope
- [x] f-strings: nested expressions, `!r`/`!s`/`!a`, format specs, `=` debug form
- [x] Type annotations parsed (and, as Monty does at runtime, ignored)
- [x] Source line and column on every node
- [ ] Remaining parser gaps — 7 fixtures still fail to parse
- [ ] Parse-error messages matching CPython's, since fixtures compare them
- [ ] `match` statements — an upstream limitation, tracked but not required

## Phase 13 — Compiler  (`src/Monty/Compilation/`)

Upstream: `bytecode/` (620 KB — the largest single area)

- [x] Instruction set (60 opcodes) and the `CodeObject` container
- [x] Scope resolution: locals, cells, globals, `global`/`nonlocal`, with the pre-pass
      that makes a name local throughout a function that assigns it anywhere
- [x] Expression and statement lowering
- [x] Control-flow lowering: loops with `break`/`continue`/`else`, exception blocks,
      `with` and its cleanup paths
- [x] Function objects: defaults, `*args`/`**kwargs`, keyword-only, closures, decorators
- [x] Class bodies as functions producing a namespace
- [x] Comprehension lowering into implicit functions
- [~] Generators lower to `Yield`/`YieldFrom`; coroutines do not
- [x] Default values evaluated at definition time in the enclosing scope, so a
      mutable default is shared across calls
- [ ] Constant folding and the peepholes upstream applies
- [ ] Compile-time limits: bytecode size, constant count, nesting depth

## Phase 14 — Runtime  (`src/Monty/Runtime/`)

Upstream: `run.rs`, `function.rs`, `heap/`, `heap_data.rs`, `resource_checks.rs`

- [x] The interpreter loop, its frame stack and its block stack
- [x] Exception raising, propagation, chaining (`__context__`, `__cause__`) and
      traceback construction
- [x] `try`/`except`/`finally` unwinding
- [x] Iterator protocol and context-manager protocol
- [x] `print` capture into stdout/stderr buffers rather than a real console
- [~] Resource limits: instruction count and recursion depth enforced; memory and wall
      clock are not
- [~] Generators run on a second, suspendable loop, so a generator holds a live
      enumerator rather than a serializable frame — this is what blocks snapshotting
- [ ] `yield`'s value being sent back in (`gen.send`)
- [ ] `asyncio` and real coroutines
- [ ] Object heap with cycle collection (currently the .NET GC)
- [ ] Tracebacks quoting the offending source line

## Phase 15 — Types and builtins  (`src/Monty/Types/`, `src/Monty/Builtins/`)

Upstream: `types/` (1.1 MB), `builtins/` (188 KB)

- [x] `int` (arbitrary precision), `float`, `bool`, `NoneType`, `Ellipsis`
- [x] `str` with 40 methods, plus the `%` and format mini-languages
- [x] `bytes`, `list`, `tuple`, `dict` (insertion-ordered), `set`, `range`, `slice`
- [x] User-defined classes — plain classes only; inheritance and metaclasses are
      upstream limitations, not oversights
- [x] Exception hierarchy with CPython's message wording
- [ ] `complex`, `bytearray`, `memoryview`, `frozenset` as a distinct type
- [x] Dunder dispatch on user classes: `__repr__` `__str__` `__bool__` `__eq__`
      `__hash__` `__lt__` `__len__` `__iter__` `__next__` `__contains__`
      `__getitem__` `__setitem__` `__delitem__`, the arithmetic dunders and their
      reflected forms, `__neg__` `__pos__` `__invert__`
- [x] Builtins: `len` `range` `print` `sorted` `enumerate` `zip` `map` `filter` `sum`
      `min` `max` `abs` `all` `any` `repr` `str` `int` `float` `bool` `list` `dict`
      `set` `tuple` `isinstance` `issubclass` `type` `getattr` `setattr` `hasattr`
      `callable` `iter` `next` `reversed` `round` `divmod` `pow` `hash` `id` `chr`
      `ord` `bin` `hex` `oct` `format` `bytes` `frozenset`
- [x] Arithmetic with Python's semantics: floor division toward negative infinity,
      modulo taking the divisor's sign, `**` promoting to float on a negative exponent

## Phase 16 — Standard library subset  (`src/Monty/Modules/`)

Upstream: `modules/` — the permitted set and nothing more.

- [x] `math`, `json`, `sys`, `typing`, `__future__`, `re`, `datetime`, `dataclasses`
- [x] `collections` (`Counter`, `defaultdict`, `namedtuple`, `OrderedDict`, `deque`)
- [x] `itertools`
- [ ] `os` and `pathlib` — routed through this repo's `IFileSystem`, so Python and bash
      see one filesystem
- [ ] `unicodedata`, `asyncio`, `gc`
- [~] `itertools.count` and `repeat` are bounded rather than infinite, since results are
      materialized rather than lazy

## Phase 17 — Host integration

Upstream: `crates/monty-types/`, `crates/monty-fs/`, bashkit's `builtins/python.rs`

- [x] `ExecutionLimits` and the `MontyRunner` facade
- [x] `Monty.Cli` — run a script or `-c` source
- [x] `Computerwelt.Cli` — the product driver, shell plus `python`
- [x] External functions — the only route to anything outside the sandbox, mirroring how
      Monty blocks filesystem, environment and network by default
- [x] `PyDataclass` — the record shape the host boundary passes structured values in
- [ ] Host object model: converting between .NET values and Monty objects
- [ ] Async external functions (`async_call`, `async_fail`)
- [ ] Snapshot and resume at an external-call boundary
- [x] Wire the `python` builtin into the shell, sharing the VFS — the payoff for porting
      both halves. `src/Computerwelt/` adds `python` / `python3` as shell commands, with
      `os`, `os.path` and `open` backed by the shell's `IFileSystem`, and 12 integration
      tests covering both directions plus the isolation guarantees.
- [ ] Share the *budget* too: Python currently gets its own instruction limit rather than
      drawing on the shell's `ExecutionBudget`
