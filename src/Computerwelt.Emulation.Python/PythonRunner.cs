using Computerwelt.Emulation.Python.Builtins;
using Computerwelt.Emulation.Python.Compilation;
using Computerwelt.Emulation.Python.Parsing;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python;

/// <summary>
/// Runs Python source in a sandbox.
/// </summary>
/// <remarks>
/// The sandbox has no filesystem, no environment and no network of its own. Anything a
/// script can reach was handed to it by the host through <see cref="Modules"/> or an
/// external function — which is the same posture the shell half of this repository takes,
/// and the reason the two can share one virtual filesystem later.
/// </remarks>
public sealed class PythonRunner
{
    /// <summary>Creates a runner with the given limits.</summary>
    public PythonRunner(ExecutionLimits? limits = null) => Limits = limits ?? ExecutionLimits.Default;

    /// <summary>The per-run resource caps.</summary>
    public ExecutionLimits Limits { get; }

    /// <summary>Modules the host makes importable, by name.</summary>
    public Dictionary<string, PyObject> Modules { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Functions the host exposes to the sandbox, by name.
    /// </summary>
    /// <remarks>
    /// This is the only route out. A script cannot open a file, read an environment
    /// variable or make a request except through a function the host put here, which is
    /// what makes the sandbox's outward surface a list the host wrote.
    /// </remarks>
    public Dictionary<string, Func<PyObject[], PyObject>> ExternalFunctions { get; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// The filesystem the program can reach, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// With no filesystem, <c>os</c> and <c>pathlib</c> are not importable and <c>open</c>
    /// is not defined — the sandbox says there is nothing there rather than offering an
    /// interface that always fails.
    /// </remarks>
    public IPyFileSystem? FileSystem { get; set; }

    /// <summary>The clock the sandbox reads, which a host may fix for reproducibility.</summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>Values the host defines in the program's global namespace before it runs.</summary>
    public Dictionary<string, PyObject> Variables { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// What <c>sys.argv</c> reports, program name first.
    /// </summary>
    /// <remarks>
    /// Left empty, <c>sys.argv</c> is <c>["&lt;script&gt;"]</c>. A host running Python as a
    /// command fills this in from the real invocation, which is why
    /// <c>python script.py --flag value</c> behaves the way a script expects.
    /// </remarks>
    public List<string> Arguments { get; } = [];

    /// <summary>
    /// What the program reads from standard input, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// With none, <c>input()</c> and <c>sys.stdin</c> are absent rather than present and
    /// empty — the same rule the filesystem follows. A host that puts Python in the middle
    /// of a pipeline sets this to whatever the previous stage produced.
    /// </remarks>
    public string? StandardInput { get; set; }

    /// <summary>Runs <paramref name="source"/> and reports what happened.</summary>
    public RunResult Run(string source, string fileName = "<stdin>")
    {
        var globals = new PyDict();
        var builtins = new PyDict();

        // The builtins close over the machine — `print` writes to its buffers, `sorted`
        // calls back into it — so the namespace is filled after the machine exists rather
        // than passed to its constructor.
        var machine = new VirtualMachine(globals, builtins, Limits);

        foreach (var (key, value) in BuiltinNamespace.Create(machine).Entries)
        {
            builtins.Set(key, value);
        }

        // One stream object serves both `sys.stdin` and `input()`, so a program that mixes
        // them reads each line once rather than seeing the input twice.
        var standardInput = StandardInput is { } text
            ? new Computerwelt.Emulation.Python.Modules.PyMemoryStream(binary: false, text)
            : null;

        foreach (var (name, module) in Computerwelt.Emulation.Python.Modules.StandardLibrary.Create(
                     machine, TimeProvider, FileSystem, Arguments, standardInput))
        {
            machine.Modules[name] = module;
        }

        if (standardInput is not null)
        {
            builtins.Set(
                new PyStr("input"),
                Computerwelt.Emulation.Python.Modules.SysModule.CreateInput(machine, standardInput));
        }

        // `open` exists only when there is somewhere to open a file.
        if (FileSystem is { } fileSystem)
        {
            builtins.Set(new PyStr("open"), Computerwelt.Emulation.Python.Modules.OsModule.CreateOpen(fileSystem));
        }

        // Host-supplied modules override the standard set, so an embedder can substitute
        // its own implementation of a name.
        foreach (var (name, module) in Modules)
        {
            machine.Modules[name] = module;
        }

        foreach (var (name, implementation) in ExternalFunctions)
        {
            globals.Set(new PyStr(name), new PyBuiltinFunction(name, implementation));
        }

        foreach (var (name, value) in Variables)
        {
            globals.Set(new PyStr(name), value);
        }

        globals.Set(new PyStr("__name__"), new PyStr("__main__"));
        globals.Set(new PyStr("__file__"), new PyStr(fileName));

        try
        {
            var module = Parser.Parse(source);
            var code = Compiler.CompileModule(module, fileName);
            var value = machine.RunModule(code);

            return new RunResult
            {
                Succeeded = true,
                Stdout = machine.Stdout,
                Stderr = machine.Stderr,
                Value = value,
                Globals = globals,
            };
        }
        catch (PythonSyntaxError error)
        {
            return new RunResult
            {
                Succeeded = false,
                Stdout = machine.Stdout,
                Stderr = machine.Stderr,
                ExitCode = 1,
                SyntaxError = error,
                Traceback = $"  File \"{fileName}\", line {error.Line}\nSyntaxError: {error.Message}",
                Globals = globals,
            };
        }
        catch (PyRaise raise) when (raise.Exception.IsInstanceOf(PyExceptionType.SystemExit))
        {
            // `sys.exit()` and a bare `raise SystemExit` are how a script *asks* to stop, so
            // they are an ordinary ending rather than a crash: no traceback, and the status
            // the script named becomes the command's exit status.
            var (code, message) = SystemExitStatus(raise.Exception);

            return new RunResult
            {
                Succeeded = true,
                Stdout = machine.Stdout,
                Stderr = message is null ? machine.Stderr : machine.Stderr + message + "\n",
                ExitCode = code,
                Exception = raise.Exception,
                Globals = globals,
            };
        }
        catch (PyRaise raise)
        {
            return new RunResult
            {
                Succeeded = false,
                Stdout = machine.Stdout,
                Stderr = machine.Stderr,
                ExitCode = 1,
                Exception = raise.Exception,
                Traceback = raise.Exception.FormatTraceback(fileName),
                Globals = globals,
            };
        }
    }

    /// <summary>
    /// Turns an uncaught <c>SystemExit</c> into a status and, when the argument was not an
    /// integer, the line CPython prints to standard error before exiting with 1.
    /// </summary>
    private static (int Code, string? Message) SystemExitStatus(PyException exception)
    {
        var argument = exception.Arguments.Count > 0 ? exception.Arguments[0] : PyNone.Instance;

        return argument switch
        {
            PyNone => (0, null),

            // Statuses are a byte on the way out of a process, and scripts do rely on
            // `sys.exit(256)` not reading as success, so the wrap is kept.
            PyInt status => ((int)(((status.Value % 256) + 256) % 256), null),
            _ => (1, argument.Display()),
        };
    }
}

/// <summary>The outcome of one run.</summary>
public sealed record RunResult
{
    /// <summary>True when the program completed without raising.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Everything the program printed.</summary>
    public required string Stdout { get; init; }

    /// <summary>Everything the program wrote to standard error.</summary>
    public required string Stderr { get; init; }

    /// <summary>
    /// The status the program asked to exit with: 0 unless it raised.
    /// </summary>
    /// <remarks>
    /// An uncaught exception is 1; <c>sys.exit(n)</c> is <c>n</c>. A host running Python as
    /// a command reports this as the command's exit status.
    /// </remarks>
    public int ExitCode { get; init; }

    /// <summary>The uncaught exception, when one ended the run.</summary>
    public PyException? Exception { get; init; }

    /// <summary>The syntax error, when the source could not be parsed.</summary>
    public PythonSyntaxError? SyntaxError { get; init; }

    /// <summary>The formatted traceback, when the run failed.</summary>
    public string? Traceback { get; init; }

    /// <summary>
    /// The value of the program's last statement, when that was a bare expression.
    /// </summary>
    /// <remarks>
    /// <see cref="PyNone"/> for anything else, so a caller can tell <c>2 + 3</c> from
    /// <c>x = 2 + 3</c> and echo only the first — which is how <c>python -c</c> behaves.
    /// </remarks>
    public PyObject Value { get; init; } = PyNone.Instance;

    /// <summary>The module namespace after the run.</summary>
    public required PyDict Globals { get; init; }

    /// <summary>The exception's class name, or null when the run succeeded.</summary>
    public string? ExceptionType => Exception?.TypeName ?? (SyntaxError is not null ? "SyntaxError" : null);
}
