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
`typescript` suite). Ratchet file: `tests/spec/baseline.json`. Upstream's `python` suite
needs a command that exists only once both halves are joined, so it lives with the
agent-operation tests instead — see below.

**Current state — shell:** solution builds clean, 156 unit tests green,
**2,521 / 2,521 conformance cases passing (100 %)**, 27 cases skipped by upstream
directive.

**Current state — python:** tokenizer, parser, bytecode compiler, VM, core types,
builtins, the stdlib subset, dunder dispatch and the external-function boundary are all in
place. **557 / 558 fixtures passing (99.8 %)**, with 40 unit tests covering what the
corpus does not reach. No host exception escapes to a script.

**Current state — joined:** 12 integration tests over one filesystem, and 210 green in
`tests/Computerwelt.AgentTests/` — 153 covering the shell and Python operations a caller
actually performs, plus upstream's 57 `python` command cases.

**Extensions:** 11 fixtures in `tests/monty-extensions/` cover behaviour upstream does not
have. They are kept apart from upstream's corpus deliberately — see below.

The one remaining fixture is a documented divergence, not a gap — see below.

| suite | passing |
|---|---|
| `bash` | 2,094 / 2,094 |
| `grep` | 95 / 95 |
| `sed` | 80 / 80 |
| `jq` | 124 / 124 |
| `awk` | 126 / 126 |
| `yq` | 26 / 26 |

## Agent-operation coverage  (`tests/Computerwelt.AgentTests/`)

The two conformance corpora prove each builtin is individually right. They say much less
about the handful of shapes a caller actually types: read a file, search a tree, patch a
source file with a short Python program, check the result. That suite was written by
replaying a real working session against this repository and turning each operation into a
test — 153 of them, plus upstream's 57-case `python` command corpus, which had never been
ported because the command it exercises only exists once both halves are joined.

Replaying found five defects the corpora between them did not:

| Found | Was | Now |
|---|---|---|
| A generator with a second `yield` in it | `ArgumentOutOfRangeException` out of the host — `yield` left no value where the compiler's `Pop` expected one | The yield expression evaluates to `None`, as it does in CPython. Covered by `GeneratorTests` |
| `sys.argv` | The constant `['<script>']`, so no script could read its own options | The invocation as written: `-c`, `-`, or the script's path, then the arguments |
| `sys.exit(n)` / `raise SystemExit(n)` | Exit status 1 and a traceback, so `python check.py \|\| handle` never fired | Status `n`, no traceback; a non-integer argument prints and exits 1 |
| `python -c "2 + 3"` | Printed nothing | Echoes the value, as upstream's corpus pins. Only for `-c`: a heredoc patch script ending in `open(p, 'w').write(s)` must not emit a stray byte count |
| `python` in the middle of a pipeline | `input` and `sys.stdin` did not exist | Both read the shell's standard input, when the program did not come from it |

## Extensions beyond Monty  (`tests/monty-extensions/`)

Some of what the agent suite wanted was not a defect but an absence: upstream Monty has no
way to match a pattern against a filename, and no way to walk a tree. Those are now
implemented, and because they are additions rather than ports they are tested apart from
upstream's corpus — a fixture in `tests/monty-extensions/` fails on upstream Monty by
construction, usually at the import. `COMPUTERWELT_SKIP_EXTENSIONS=1` switches the folder
off, which is the check that the port still stands on upstream's corpus alone.

| Added | Surface | Fixture |
|---|---|---|
| `glob` | `glob`, `iglob`, `escape`, `has_magic`; `**` with `recursive=`, `root_dir=`, `include_hidden=` | `glob__patterns.py` |
| `fnmatch` | `fnmatch`, `fnmatchcase`, `filter`, `translate` — no filesystem needed, so importable without one | `fnmatch__patterns.py` |
| `os.walk` | top-down and bottom-up, `onerror`, and pruning via the directory list | `os__walk.py` |
| `os.scandir` | `DirEntry` with `name`, `path`, `is_dir`, `is_file`, `stat`, `__fspath__`; a context manager | `os__scandir.py` |
| `os.path` | `relpath`, `commonpath`, `commonprefix`, `realpath`, `normcase`, `lexists`, `getmtime`, `expanduser`; `normpath` now collapses `..` | `os__path_extended.py` |
| `import os.path` | `os.path` and `posixpath` are importable names for the object `os.path` already was; `import a.b` binds `a`, as CPython does | `os__path_extended.py` |
| `Path.glob` / `rglob` / `match` / `full_match` / `walk` | upstream's `Path` had `iterdir` and nothing more | `pathlib__glob.py` |
| `os.walk(..., max_depth=N)` | not CPython's either — a bound the caller asks for, which ends the walk cleanly | `os__walk_depth.py` |
| `io` | `io.open`, `StringIO`, `BytesIO`, `UnsupportedOperation` | `io__module.py` |
| `sys.argv`, `sys.exit`, `input`, `sys.stdin` | the host-facing four from the previous round | `sys__*.py` |

One engine sits behind `fnmatch`, `glob` and `Path.glob`, because CPython's three agree on
what a pattern means and differ only in what they match it against. `os.walk` is iterative
and lazy: iterative because recursion would spend the *host's* stack on the depth of a tree
the program chose, and lazy because pruning only works if the descent happens after the
caller's turn.

### Depth is bounded twice, for two different reasons

| | What it is | What happens at it |
|---|---|---|
| `FsLimits.MaxDepth` (64) | the shell filesystem's cap on the path it will **create** | `mkdir` refuses — the tree simply cannot get deeper |
| `ExecutionLimits.MaxDirectoryDepth` (64) | how deep a **traversal** will descend | raises `OSError`, because a walk that quietly stopped part-way would report a subset of the tree as though it were all of it |
| `os.walk(..., max_depth=N)` | the **caller's** own bound, relative to `top` | ends the walk cleanly — this is someone asking for less, not a limit being hit |

The first is the real containment: nothing can walk depth that cannot exist. It now covers
the whole path rather than just directories — a file sits one level below the directory
holding it, so capping directories alone left the deepest thing in the tree one past the
limit. The second matters only for a host that supplies its own `IPyFileSystem` over
storage this sandbox did not build, where a tree can be arbitrarily deep or, through a link
to its own ancestor, bottomless. `DepthLimitTests` walks exactly that filesystem.

Making the traversal cap useful turned up a related hole: `ShellFileSystem` translated only
some failures into Python exceptions, so `os.makedirs` past the depth limit threw a
`FileSystemException` straight out of `ExecAsync` — a host exception leaving the sandbox
rather than an error the program could catch. Every call now goes through one guarded
chokepoint that maps `FileSystemErrorKind` onto the matching Python exception.

Recorded, not fixed, because upstream defines the surface and this port follows it:

| Absent | Note |
|---|---|
| `open(..., newline='')` | Refused, though nothing here translates line endings and it would describe what already happens. `tests/monty-spec/open__fs.py` pins the refusal; binary mode is the way to ask for exact bytes |
| `shutil`, `argparse`, `textwrap`, `difflib`, `tempfile`, `csv`, `hashlib`, `base64`, `string`, `functools`, … | Not ported. `StandardLibrarySurfaceTests` lists the set in both directions, so adding one is a deliberate edit rather than a silent widening |

## Known divergences

| Fixture | Why |
|---|---|
| `id__non_overlapping_lifetimes_same_types.py` | Asserts `id([]) == id([])`: upstream allocates from a heap of recycled slots, so a temporary's id is handed to the next object of the same shape. Here an object's identity is the host runtime's, and the host frees objects when its collector chooses rather than when the last reference drops — so an id is never recycled. Reproducing the assertion would mean either making distinct live objects share an id or forcing a collection inside `id()`, and a sandbox that can make its host collect on demand is a denial-of-service vector. Upstream's own corpus marks the sibling file `xfail=cpython`, so this family tests heap behaviour rather than language behaviour. |

---

## Phase 0 — Groundwork

- [x] Vendor upstream into `.reference/bashkit/` (CI/CD + eval result blobs stripped)
- [x] `CLAUDE.md` — architecture, invariants, Rust→C# idiom map
- [x] `todo.md` — this ledger
- [x] Solution, `Directory.Build.props`, `Directory.Packages.props` (net10.0, nullable,
      warnings-as-errors, central package management)
- [x] Copy upstream spec cases into `tests/spec/`
- [x] `Computerwelt.Emulation.Bash.SpecTests` harness: spec file parser + ratchet runner + `report` mode

## Phase 1 — Core primitives  (`src/Computerwelt.Emulation.Bash/Core/`)

Upstream: `stream.rs`, `error.rs`, `interpreter/state.rs`, `fs/posix.rs`

- [x] `VPath` — POSIX-only virtual path value type (normalize, join, parent, filename,
      extension, absolute/relative, `..` resolution without touching the host FS)
- [x] `StreamData` — byte-oriented stdio payload with lossy UTF-8 view
- [x] `ExecResult` — stdout/stderr/exit code/control flow/truncation flags
- [x] `ControlFlow` — none / break(n) / continue(n) / return(code) / exit(code)
- [x] `ShellException` + error taxonomy (`Error` variants from `error.rs`)
- [x] `ExitCodes` constants (127 not-found, 126 not-executable, 2 usage, 128+n signals)

## Phase 2 — Limits  (`src/Computerwelt.Emulation.Bash/Limits/`)

Upstream: `limits.rs` (73 KB), `profile.rs`

- [x] `ExecutionLimits` — commands, loop iterations, total loop iterations, function
      depth, timeout, parser timeout, input size, output size, work units
- [x] `ExecutionBudget` — shared counters for commands, loop iterations, work units,
      parser fuel, function/nesting depth, wall-clock deadline and cancellation
- [ ] `SessionLimits` + `ExecutionCounters` — cross-exec accounting
- [ ] `MemoryLimits` / `MemoryBudget` — live intermediate byte tracking with leases
- [ ] `ExecutionProfile` presets (strict / default / permissive)

## Phase 3 — Virtual filesystem  (`src/Computerwelt.Emulation.Bash/FileSystems/`)

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

## Phase 4 — Parser  (`src/Computerwelt.Emulation.Bash/Parsing/`)

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

## Phase 5 — Interpreter  (`src/Computerwelt.Emulation.Bash/Interpreter/`)

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
- [ ] Two array details bash has and this does not, both found while benchmarking and both
      pre-existing: a scalar assigned over an array replaces element 0 only in bash
      (`x=(a b c); x=z` leaves `z b c`) where this collapses the array to one element; and
      an array assigned nothing is *unset* in bash (`x=(); echo ${x+set}` prints nothing)
      where this reports it as set
- [~] `trap`: `EXIT` and `ERR` fire; `DEBUG` and `RETURN` do not
- [ ] Job control simulation (`&`, `jobs`, `wait`, `%1`)
- [~] `set -e` fires and is suppressed after `&&`/`||`/`!`; the full context list is unverified
- [~] `set -x` emits `+ cmd` to stderr; `PS4` and structured `TraceEvent` not yet
- [ ] Process substitution execution
- [ ] `time` keyword, `coproc`
- [x] `IFS` field splitting with the whitespace / non-whitespace separator distinction

## Phase 6 — Builtins  (`src/Computerwelt.Emulation.Bash/Builtins/`)

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

## Phase 7 — Public facade  (`src/Computerwelt.Emulation.Bash/`)

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
- [x] Host commands — `IShellExtension` for a set granted as one unit, `ICommandResolver`
      for a name space too large to enumerate, `DelegateBuiltin` for the one-liner, and
      `WithoutBuiltin` for a deny-list. See *Extending the sandbox* below
- [x] Host commands reach the environment — `BuiltinContext` gained `Environment`,
      `ReadTextAsync`, `WriteTextAsync` and `ReadTextIfExistsAsync`, so custom functionality
      written in C# is a few lines over the sandbox's own filesystem
- [ ] `BuiltinRegistry` — a host-owned registry that can be mutated after `Build()`, for a
      REPL or an FFI binding that registers callbacks at runtime (upstream has one)
- [ ] Hook interceptors (`before_exec`, `after_exec`, `before_tool`, `on_exit`)
- [ ] NuGet packaging metadata, symbols, deterministic build
- [ ] Public API surface tests (`PublicAPI.Shipped.txt`)
- [x] Benchmarks (`BenchmarkDotNet`) mirroring `crates/bashkit-bench` — `bench/Computerwelt.Benchmarks/`
      carries upstream's 96 cases verbatim, plus session, parser, filesystem and Python
      micro-benchmarks and a stopwatch mode for the edit loop. See *Performance* below

## Phase 10 — Hardening

- [ ] Port the 280+ threat-model mitigations (`.reference/bashkit/knowledge/security/`)
- [ ] Property tests for parser/expansion (`proptest_security.rs` equivalent, FsCheck)
- [ ] Fuzz targets for lexer/parser/arithmetic (`fuzz/`)
- [x] Fill in the ratchet to 100 % of non-skipped spec cases

---

## Performance

Measured with `bench/Computerwelt.Benchmarks/`, which carries bashkit's own 96-case corpus
(`crates/bashkit-bench/src/cases.rs`) unedited. The numbers below come from that project on
one 4-CPU machine and only compare with each other; the corpus row builds a session per
case, as upstream's in-process runner does.

| | before | after |
|---|--:|--:|
| corpus, 96 cases | 71.0 ms · 45.0 MB | 64.8 ms · 26.7 MB |
| `Bash.Create()` | 9.0 µs · 27.4 KB | 3.8 µs · 8.9 KB |
| build a session and `echo hello` | 13.4 µs · 32.3 KB | 5.6 µs · 12.0 KB |
| `echo hello` on a warm session | 2.5 µs · 4.7 KB | 1.7 µs · 3.1 KB |
| python: 500 f-strings and a dict tally | 39.0 ms · 2.8 MB | 1.25 ms · 1.1 MB |
| python: `fib(18)` | 7.0 ms · 9.7 MB | 4.8 ms · 5.0 MB |

What changed, in the order the measurements pointed at:

- [x] **The default command table is shared.** Building a session registered ~160 builtin
      instances into a fresh dictionary; a session that takes the standard set unchanged
      now gets one shared `FrozenDictionary`. Sound because a builtin is already required
      to be stateless and thread-safe — the same instance serves every execution and every
      subshell of one session. Anything the host altered still builds its own table
- [x] **A variable allocates storage only when it needs it.** Every `ShellVariable` eagerly
      built a `SortedDictionary` *and* a `Dictionary`, so a scalar cost four objects. A
      scalar — or a one-element array such as `FUNCNAME` — is now one string field, and the
      map appears when a second subscript does
- [x] **A fork copies from storage rather than replaying assignments.** `CloneVariable`
      rebuilt each array through its string subscripts; `ShellVariable.Clone` copies the
      dictionaries. The scope copy is also pre-sized, which it never was
- [x] **An inert literal skips expansion.** A word that is one unquoted literal with no
      expansion trigger in it — a command name, a flag, most arguments — is handed back as
      it stands instead of going through field builders, escaping and an unescape pass.
      `SearchValues<char>` answers "does this contain a trigger" in one vectorised scan
- [x] **The word parser has the same fast path**, so a plain word is not rebuilt through a
      `StringBuilder` into a copy of the string the lexer already produced
- [x] **Quoted text is escaped in runs**, not character by character, again via
      `SearchValues<char>`
- [x] **Python's integer→string guard is a constant.** `BigInteger.Pow(10, 4300)` was
      evaluated on *every* integer that became a string — `str(i)`, an f-string, a `print`.
      Hoisting it made string-producing Python 14–31× faster
- [x] **Python binds an ordinary call directly.** Argument binding allocated a list, a set,
      a dict and two LINQ chains per call and searched `LocalNames` by string per
      parameter. A call with one argument per parameter and nothing to reconcile now fills
      the slots from a table cached on the code object

What the measurements still point at, in value order:

- [ ] **Copy-on-write scopes.** A command substitution forks the whole variable set —
      ~5 KB — to run `$(fib $((n-1)))`, and the corpus's heaviest cases are nothing but
      that. Sharing until first write is the remaining large win, and the hazard is
      precise: a caller that obtains a `ShellVariable` and mutates it in place would write
      through into the parent, so it needs the mutation path narrowed first. Isolation is
      a security property here, so this is worth doing carefully or not at all
- [ ] **Parse once per script, not once per evaluation.** A substitution body and an
      arithmetic expression are re-parsed on every execution, so a loop re-parses per
      iteration. The cache belongs on the AST node, whose lifetime is exactly the script's;
      it must keep charging the parse budget on a hit, or it would quietly weaken a limit
- [ ] **Python's `int` is a `BigInteger` and every result is a fresh `PyInt`.** A small-int
      cache and a `long` fast path are the standard answers; both are wide changes and the
      corpus is the check
- [ ] Per-command work in the interpreter — the argument-list copy, the `FUNCNAME` rebuild
      on entry *and* exit of every function — is small individually and adds up in loops

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
- [x] `src/Computerwelt.Emulation.Python/`, `src/Computerwelt.Emulation.Python.Cli/` and `tests/Computerwelt.Emulation.Python.SpecTests/`
- [x] Fixture parser: `assert`-only cases, `# Raise=`, `TRACEBACK:`, `# xfail=` directives
- [x] Ratchet runner + `tests/monty-spec/baseline.json`

## Phase 12 — Front end  (`src/Computerwelt.Emulation.Python/Parsing/`)

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

## Phase 13 — Compiler  (`src/Computerwelt.Emulation.Python/Compilation/`)

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

## Phase 14 — Runtime  (`src/Computerwelt.Emulation.Python/Runtime/`)

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

## Phase 15 — Types and builtins  (`src/Computerwelt.Emulation.Python/Types/`, `src/Computerwelt.Emulation.Python/Builtins/`)

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

## Phase 16 — Standard library subset  (`src/Computerwelt.Emulation.Python/Modules/`)

Upstream: `modules/` — the permitted set and nothing more.

- [x] `math`, `json`, `sys`, `typing`, `__future__`, `re`, `datetime`, `dataclasses`
- [x] `collections` (`Counter`, `defaultdict`, `namedtuple`, `OrderedDict`, `deque`)
- [x] `itertools`
- [x] `os` and `pathlib` — routed through this repo's `IFileSystem`, so Python and bash
      see one filesystem
- [x] `unicodedata`, `asyncio`, `gc` — `unicodedata` carries its own Unicode 16.0.0 tables
      (categories, combining classes, names, decompositions) and implements normalization
      itself, so no answer depends on the host's ICU or on its globalization mode
- [~] `itertools.count` and `repeat` are bounded rather than infinite, since results are
      materialized rather than lazy

## Phase 17 — Host integration

Upstream: `crates/monty-types/`, `crates/monty-fs/`, bashkit's `builtins/python.rs`

- [x] `ExecutionLimits` and the `PythonRunner` facade
- [x] `Computerwelt.Emulation.Python.Cli` — run a script or `-c` source
- [x] `Computerwelt.Cli` — the product driver, shell plus `python`
- [x] External functions — the only route to anything outside the sandbox, mirroring how
      Monty blocks filesystem, environment and network by default
- [x] `PyDataclass` — the record shape the host boundary passes structured values in
- [x] Host libraries — `PythonLibrary`, built per run and on first import, written in C#
      (`FromFactory`, `FromFunctions`) or in Python (`FromSource`). See *Extending the
      sandbox* below
- [x] Host functions with the environment — `PythonHostContext` carries the run's
      filesystem, working directory, environment, limits and clock, so custom functionality
      implemented in C# runs *inside* the sandbox rather than beside it
- [ ] Host object model: converting between .NET values and Monty objects — host code still
      builds its `PyObject`s by hand
- [ ] Async external functions (`async_call`, `async_fail`)
- [ ] Snapshot and resume at an external-call boundary
- [x] Wire the `python` builtin into the shell, sharing the VFS — the payoff for porting
      both halves. `src/Computerwelt/` adds `python` / `python3` as shell commands, with
      `os`, `os.path` and `open` backed by the shell's `IFileSystem`, and 12 integration
      tests covering both directions plus the isolation guarantees.
- [ ] Share the *budget* too: Python currently gets its own instruction limit rather than
      drawing on the shell's `ExecutionBudget`


---

## Extending the sandbox

Both halves take host extensions, and the two APIs are shaped by the same rule: a host adds
*vocabulary*, never *authority*. Nothing registered here gets a capability the sandbox did
not already have — host code runs under the same limits, against the same virtual
filesystem, with no process, no host disk and no network of its own.

| Point | Shape | Where |
|---|---|---|
| `WithBuiltin` | `IBuiltin`, or a delegate | `src/Computerwelt.Emulation.Bash/Extensibility/DelegateBuiltin.cs` |
| `WithExtension` | `IShellExtension` | `.../Extensibility/IShellExtension.cs` |
| `WithCommandResolver` | `ICommandResolver` | `.../Extensibility/ICommandResolver.cs` |
| `WithoutBuiltin` | a name | `src/Computerwelt.Emulation.Bash/BashBuilder.cs` |
| `PythonRunner.Libraries`, `PythonOptions.Libraries` | `PythonLibrary` | `src/Computerwelt.Emulation.Python/Extensibility/` |
| `PythonRunner.HostFunctions`, `PythonOptions.HostFunctions` | `Func<PythonHostContext, PyObject[], PyObject>` | `.../Extensibility/PythonHostContext.cs` |

### The four decisions worth recording

**A resolver is consulted last.** After shell functions, after registered commands, and
after the search for a script in the virtual filesystem — the same order upstream's
`CommandResolver` uses. It can therefore fill an open-ended name space without being able to
shadow anything that already exists, and the cost is that its names are not enumerable:
`type`, `command -v` and `Bash.BuiltinNames` do not list them. That is the honest report,
since the host cannot enumerate the space either.

**Withholding is applied after every registration.** `WithoutBuiltin` runs last in `Build()`,
so the order the host called things in cannot matter and an extension cannot re-grant a name
the host took away. The name ends up absent rather than present-and-refusing, which is the
posture the rest of the sandbox takes: a capability that was never registered is a much
stronger property than one that is registered and guarded.

**A Python library is a recipe, not a module.** `PythonRunner.Modules` hands one object to
every run; that is right for a constant table and wrong for anything a program can mutate,
because one tenant's state would become the next one's. `Libraries` asks for a fresh module
on first import of each run — which is also the only moment at which a library *written in
Python* can be built, since running its source needs the machine that is asking. Lazy
resolution goes through `VirtualMachine.ModuleResolver`; a name no library claims is still
`ModuleNotFoundError`, so the importable set stays the closed list the host wrote.

**Host C# is handed the environment, not a way to find one.** `BuiltinContext` and
`PythonHostContext` carry the same set — the virtual filesystem, the working directory, the
environment, the limits, the clock — and both read it live, so a `cd` the script performed
is where host code finds itself. `PythonHostContext.RequireFileSystem` turns "there is no
storage" into a Python `OSError`, because a host exception reaching a sandboxed program is
the sandbox leaking rather than an error the program can handle. Nothing compiles C# from
inside a script: host code is registered by the host, before the session is built.

### Coverage

| Suite | Cases |
|---|---|
| `Computerwelt.Emulation.Bash.Tests/ExtensibilityTests` | 22 — dispatch, override, withholding, resolver ordering, budget charging, the context helpers, two sessions sharing one command |
| `Computerwelt.Emulation.Python.Tests/ExtensibilityTests` | 27 — host and source libraries, host functions over a filesystem, per-run freshness, laziness, cycles, a library that will not compile, limits |
| `Computerwelt.Tests/ExtensibilityTests` | 12 — a host command, a host library and a host function over one filesystem |

`samples/Computerwelt.Sample.Extensibility/` is every point in one runnable program.
