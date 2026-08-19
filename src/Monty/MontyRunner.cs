using Monty.Builtins;
using Monty.Compilation;
using Monty.Parsing;
using Monty.Runtime;

namespace Monty;

/// <summary>
/// Runs Python source in a sandbox.
/// </summary>
/// <remarks>
/// The sandbox has no filesystem, no environment and no network of its own. Anything a
/// script can reach was handed to it by the host through <see cref="Modules"/> or an
/// external function — which is the same posture the shell half of this repository takes,
/// and the reason the two can share one virtual filesystem later.
/// </remarks>
public sealed class MontyRunner
{
    /// <summary>Creates a runner with the given limits.</summary>
    public MontyRunner(ExecutionLimits? limits = null) => Limits = limits ?? ExecutionLimits.Default;

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

        foreach (var (name, module) in Monty.Modules.StandardLibrary.Create(machine, TimeProvider, FileSystem))
        {
            machine.Modules[name] = module;
        }

        // `open` exists only when there is somewhere to open a file.
        if (FileSystem is { } fileSystem)
        {
            builtins.Set(new PyStr("open"), Monty.Modules.OsModule.CreateOpen(fileSystem));
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
            machine.RunModule(code);

            return new RunResult
            {
                Succeeded = true,
                Stdout = machine.Stdout,
                Stderr = machine.Stderr,
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
                SyntaxError = error,
                Traceback = $"  File \"{fileName}\", line {error.Line}\nSyntaxError: {error.Message}",
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
                Exception = raise.Exception,
                Traceback = raise.Exception.FormatTraceback(fileName),
                Globals = globals,
            };
        }
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

    /// <summary>The uncaught exception, when one ended the run.</summary>
    public PyException? Exception { get; init; }

    /// <summary>The syntax error, when the source could not be parsed.</summary>
    public PythonSyntaxError? SyntaxError { get; init; }

    /// <summary>The formatted traceback, when the run failed.</summary>
    public string? Traceback { get; init; }

    /// <summary>The module namespace after the run.</summary>
    public required PyDict Globals { get; init; }

    /// <summary>The exception's class name, or null when the run succeeded.</summary>
    public string? ExceptionType => Exception?.TypeName ?? (SyntaxError is not null ? "SyntaxError" : null);
}
