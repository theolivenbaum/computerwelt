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
| `Computerwelt.Playwright` | optional: Playwright's Python `sync_api` over the .NET driver |

Each project's root namespace is its package name, so a type's namespace says which package
it ships in.

## Extending it

Everything a session can reach is decided when it is built and cannot be widened
afterwards. There are four extension points, and each of them adds vocabulary without
adding authority — a host command runs under the same limits and against the same virtual
filesystem as everything else.

```csharp
var bash = Bash.CreateBuilder()

    // A related set of commands, granted as one unit.
    .WithExtension(new TicketsExtension())

    // One command, straight from a delegate.
    .WithBuiltin("greet", context => ExecResult.Ok($"Hello, {context.Arguments[0]}!\n"))

    // A name space too large to enumerate, answered one name at a time and consulted last.
    .WithCommandResolver(new RunbookResolver(catalogue))

    // Withheld: the session has no such command, and no script can discover one.
    .WithoutBuiltins("tar", "curl")

    // Python libraries, importable from the `python` command.
    .WithPython(new PythonOptions
    {
        Libraries =
        [
            PythonLibrary.FromFactory("tickets", context => …),      // written in C#
            PythonLibrary.FromSource("formatting", "def table(…): …") // written in Python
        ],
    })

    .Build();
```

| Point | Shape | For |
|---|---|---|
| `WithBuiltin` | `IBuiltin`, or a delegate | one command |
| `WithExtension` | `IShellExtension` | a domain vocabulary, granted or withheld as a unit |
| `WithCommandResolver` | `ICommandResolver` | an open-ended name space, resolved on demand |
| `WithoutBuiltin` | a name | taking a command away |
| `PythonOptions.Libraries` | `PythonLibrary` | a Python module, written in C# or in Python |
| `PythonOptions.HostFunctions` | `Func<PythonHostContext, PyObject[], PyObject>` | a C# function a Python program calls without importing anything |

### Implementing functionality in C#

Host code is handed the sandbox's environment, so "write it in C#" means writing it
*inside* the sandbox rather than beside it. A shell command gets a `BuiltinContext`; C#
called from Python gets a `PythonHostContext`. Both carry the same things — the virtual
filesystem, the working directory as it stands right now, the environment the script
exported, the run's limits and its clock — and nothing else. There is no host disk, no
process and no network behind either of them.

```csharp
// A command, in C#.
.WithBuiltin("upper", async (context, token) =>
{
    var text = await context.ReadTextAsync(context.Arguments[0], token);
    await context.WriteTextAsync(context.Arguments[1], text.ToUpperInvariant(), token);
    return ExecResult.Success;
})

// A function Python can call, in C#.
.WithPython(new PythonOptions
{
    HostFunctions = new()
    {
        ["disk_usage"] = (context, args) => new PyInt(Total(context.RequireFileSystem(), args)),
    },
})
```

```bash
echo hi > /a.txt && upper /a.txt /b.txt     # the C# command
python -c "print(disk_usage('/'))"          # the C# function, same filesystem
```

Because the context reads the environment rather than a snapshot of it, a `cd` earlier in
the script is where host code finds itself too. And because the sandbox has no ambient
anything, host code that needs storage where there is none says so: `RequireFileSystem()`
raises a Python `OSError` the program can catch, rather than letting a host exception
escape into it.

Two rules matter more than the rest. A **builtin is shared** by every execution of every
session it is registered with, so it must be stateless and thread-safe — everything one
invocation can see arrives in its `BuiltinContext`. A **library is built per run**, so
module-level state cannot ride from one `python` invocation to the next, or from one tenant
to another; that is the difference between `Libraries` and putting an object in
`PythonRunner.Modules`.

A resolver is consulted last — after shell functions, registered commands and the search
for a script — so it can extend the vocabulary but never shadow it, and its names are not
enumerable. `WithoutBuiltin` is applied after every registration, so a withheld name loses
to nothing, and it is absent rather than refusing: `type`, `command -v` and
`Bash.BuiltinNames` do not report it.

[`samples/Computerwelt.Sample.Extensibility/`](samples/Computerwelt.Sample.Extensibility)
is all four points in one runnable program:

```bash
dotnet run --project samples/Computerwelt.Sample.Extensibility
```

## Driving a browser

`Computerwelt.Playwright` is an optional package that makes Playwright's **Python
`sync_api`** importable inside the sandbox, backed by Microsoft's .NET Playwright bindings.
A script writes exactly what it would write anywhere else:

```python
from playwright.sync_api import sync_playwright, expect

with sync_playwright() as p:
    page = p.chromium.launch().new_page()
    page.goto("https://example.com/")

    expect(page.get_by_role("heading")).to_have_text("Example Domain")
    page.screenshot(path="/report/home.png")
```

…and `/report/home.png` is a file in the *virtual* filesystem, which the shell around the
script can `ls`, `cat` and pipe. Nothing reached the host's disk.

```csharp
await PlaywrightBrowsers.InstallAsync([PlaywrightBrowser.Chromium]);   // once, at start-up

await using var session = await PlaywrightSession.CreateAsync(new PlaywrightOptions
{
    Browsers = [PlaywrightBrowser.Chromium],
    AllowedHosts = ["*.example.com", "localhost:8080"],
});

var bash = Bash.CreateBuilder().WithPlaywright(session).Build();
```

The browser is a process, and this repository's first rule is that the sandbox spawns none.
The rule is not weakened but moved: the **host** starts the browser, before any script runs,
and `p.chromium.launch()` hands back a handle to what is already running rather than
starting anything — a launch option a script passes is refused by name rather than ignored.
Everything else survives intact, and by construction: `AllowedHosts` is empty by default and
enforced as a request filter on every context (so a redirect, an iframe or a `fetch()` is
checked too, not only the navigation a script typed); every path in the API is a virtual
one; contexts, pages and transfers are capped; a context per script is what keeps two
tenants apart; and no driver exception reaches a program untranslated.

[`src/Computerwelt.Playwright/README.md`](src/Computerwelt.Playwright/README.md) has the
full mapping, the caps, and the list of what is deliberately absent and why.

## What "sandboxed" means here

- **No process spawning.** Every command is a managed implementation. There is no `PATH`
  lookup, no `fork`, no `exec`. The one exception is the optional
  `Computerwelt.Playwright` package, where the *host* starts a browser before any script
  runs — a script still cannot start anything.
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
| `src/Computerwelt.Playwright/` | optional: Playwright's Python `sync_api` over the .NET driver |
| `tests/Computerwelt.Emulation.Python.Tests/` | Python unit tests |
| `tests/Computerwelt.Emulation.Python.SpecTests/` | Python conformance runner |
| `tests/Computerwelt.Tests/` | integration: both interpreters over one filesystem |
| `tests/Computerwelt.AgentTests/` | the operations a caller performs, end to end, plus upstream's `python` command corpus |
| `tests/Computerwelt.Emulation.Bash.Tests/` | shell unit tests |
| `tests/Computerwelt.Emulation.Bash.SpecTests/` | shell conformance runner |
| `tests/Computerwelt.Playwright.Tests/` | the navigation policy, and browser scenarios written as Python |
| `tests/spec/` | 2,521 golden shell cases carried over from bashkit |
| `tests/monty-spec/` | 568 Python fixtures carried over from monty |
| `tests/monty-extensions/` | fixtures for behaviour monty does **not** have, kept apart on purpose |
| `samples/Computerwelt.Sample.Extensibility/` | a runnable tour of the four extension points |
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

`tests/monty-extensions/` is the opposite arrangement: absolute rather than ratcheted, and
deliberately separate, because every fixture in it exercises something upstream Monty does
not have — `glob`, `fnmatch`, `os.walk`, `os.scandir`, `io`, `sys.argv`. Run one against
upstream and it fails at the import. To check that the port still stands on upstream's
corpus alone:

```bash
COMPUTERWELT_SKIP_EXTENSIONS=1 dotnet test
```

Publishing is `.devops/build-nuget.yml`, an Azure DevOps pipeline on a push to `main`, and
it is the only thing that pushes packages. It runs the whole suite before it packs, and the
version is CalVer computed in the pipeline (`yy.M.<build id>`), so no release version is
committed anywhere in the repository.

## Status

| | conformance | notes |
|---|---|---|
| shell | **2,521 / 2,521** | 73 commands implemented; 27 cases skipped by upstream directive |
| python | **557 / 558** | parser, bytecode compiler, VM, types, builtins, the stdlib subset, dunders |
| joined | **210 / 210** | 153 agent-operation tests plus upstream's 57 `python` command cases |
| extensions | **11 / 11** | fixtures for what this port adds beyond monty |
| playwright | **34 / 34** | the navigation policy, and 21 browser scenarios written as Python |

The one Python fixture that does not pass asserts that a temporary's `id()` is handed to
the next object of the same shape — an artifact of upstream's slot-recycling heap. Object
identity here is the host runtime's, and an id is never recycled; the reasoning is in
[`todo.md`](todo.md).

The two halves share one virtual filesystem: `src/Computerwelt/` adds `python` as a shell
command whose `os`, `os.path` and `open` are backed by the shell's `IFileSystem`.

`tests/Computerwelt.AgentTests/` covers that seam from the caller's side — not features but
*operations*: read a file, search a tree, patch a source file with a heredoc Python
program, check the result. The cases were taken from real sessions rather than invented,
and the defects they surfaced (a generator with two `yield`s crashing the host, a constant
`sys.argv`, `sys.exit(n)` not reaching the shell, no `sys.stdin`) were all in code both
conformance corpora already covered. [`todo.md`](todo.md) lists what it found and what it
deliberately leaves absent.

See [`todo.md`](todo.md) for the ledger and [`CLAUDE.md`](CLAUDE.md) for the architecture
and the invariants that define "correct".

## Licence

MIT, matching both upstreams. Attribution is preserved under each vendored tree.
