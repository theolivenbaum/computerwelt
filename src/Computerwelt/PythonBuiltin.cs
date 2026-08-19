using System.Text;
using Bashkit;
using Bashkit.Builtins;
using Monty;
using Monty.Runtime;

namespace Computerwelt;

/// <summary>
/// The shell's <c>python</c> command, backed by the embedded interpreter.
/// </summary>
/// <remarks>
/// <para>
/// The script runs against the <i>shell's</i> virtual filesystem and inside the shell's
/// execution budget, so <c>echo hi &gt; f.txt &amp;&amp; python -c "print(open('f.txt').read())"</c>
/// works and neither half can reach the host. That is the reason both interpreters were
/// ported into one library.
/// </para>
/// <para>
/// Python's exit status follows the shell convention: 0 on success, 1 on an uncaught
/// exception, and the traceback goes to stderr exactly as CPython prints it.
/// </para>
/// </remarks>
public sealed class PythonBuiltin : IBuiltin
{
    private readonly PythonOptions _options;

    /// <summary>Creates the builtin under <paramref name="name"/>.</summary>
    public PythonBuiltin(string name = "python", PythonOptions? options = null)
    {
        Name = name;
        _options = options ?? new PythonOptions();
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string? LlmHint =>
        "python: Runs Python in-process against the same virtual filesystem. "
        + "Supports `python file.py` and `python -c 'code'`. A subset of the stdlib is available.";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var (source, scriptName, arguments, error) = await ResolveSourceAsync(context, cancellationToken);

        if (error is not null)
        {
            return error;
        }

        var runner = new MontyRunner(new Monty.Runtime.ExecutionLimits
        {
            MaxInstructions = _options.MaxInstructions,
            MaxRecursionDepth = _options.MaxRecursionDepth,
        });

        // The filesystem modules close over the shell's state, so `os.getcwd()` follows a
        // `cd` that happened earlier in the same script.
        runner.Modules["os"] = PythonFileSystem.CreateOsModule(context.FileSystem, () => context.State.WorkingDirectory);

        foreach (var (name, module) in _options.AdditionalModules)
        {
            runner.Modules[name] = module;
        }

        foreach (var (name, function) in _options.ExternalFunctions)
        {
            runner.ExternalFunctions[name] = function;
        }

        runner.ExternalFunctions["open"] = BuildOpen(context);

        var result = runner.Run(source!, scriptName);
        _ = arguments;

        if (result.Succeeded)
        {
            return new ExecResult
            {
                Stdout = StreamData.FromText(result.Stdout),
                Stderr = StreamData.FromText(result.Stderr),
            };
        }

        return new ExecResult
        {
            Stdout = StreamData.FromText(result.Stdout),
            Stderr = StreamData.FromText(result.Stderr + result.Traceback + "\n"),
            ExitCode = ExitCodes.Failure,
        };
    }

    private static Func<PyObject[], PyObject> BuildOpen(BuiltinContext context)
    {
        var open = PythonFileSystem.CreateOpen(context.FileSystem, () => context.State.WorkingDirectory);
        return arguments => ((PyBuiltinFunction)open).Invoke(arguments);
    }

    private static async ValueTask<(string? Source, string ScriptName, List<string> Arguments, ExecResult? Error)>
        ResolveSourceAsync(BuiltinContext context, CancellationToken cancellationToken)
    {
        var index = 0;

        while (index < context.Arguments.Count)
        {
            var argument = context.Arguments[index];

            if (argument == "-c")
            {
                if (index + 1 >= context.Arguments.Count)
                {
                    return (null, string.Empty, [], ExecResult.Usage("python", "argument to -c is missing"));
                }

                return (context.Arguments[index + 1], "<string>",
                    [.. context.Arguments.Skip(index + 2)], null);
            }

            // Flags that change nothing here are accepted and ignored, so that a script
            // written for CPython still runs.
            if (argument is "-u" or "-B" or "-E" or "-s" or "-S" or "-O" or "-I")
            {
                index++;
                continue;
            }

            if (argument == "--version" || argument == "-V")
            {
                return (null, string.Empty, [], ExecResult.Ok("Python 3.12.0 (monty)\n"));
            }

            break;
        }

        // No script, or an explicit `-`, means read the program from standard input —
        // which is what `python - <<'EOF'` in a shell script relies on.
        if (index >= context.Arguments.Count || context.Arguments[index] == "-")
        {
            return (context.StdinText, "<stdin>",
                [.. context.Arguments.Skip(Math.Min(index + 1, context.Arguments.Count))], null);
        }

        var path = context.ResolvePath(context.Arguments[index]);

        try
        {
            var bytes = await context.FileSystem.ReadFileAsync(path, cancellationToken);

            return (Encoding.UTF8.GetString(bytes), context.Arguments[index],
                [.. context.Arguments.Skip(index + 1)], null);
        }
        catch (FileSystemException)
        {
            return (null, string.Empty, [], ExecResult.Error(
                $"python: can't open file '{context.Arguments[index]}': No such file or directory\n",
                ExitCodes.Usage));
        }
    }
}

/// <summary>How the <c>python</c> builtin is configured.</summary>
public sealed record PythonOptions
{
    /// <summary>Maximum bytecode instructions per invocation.</summary>
    public long MaxInstructions { get; init; } = 20_000_000;

    /// <summary>Maximum Python call depth.</summary>
    public int MaxRecursionDepth { get; init; } = 200;

    /// <summary>Extra modules the host makes importable from Python.</summary>
    public Dictionary<string, PyObject> AdditionalModules { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Extra functions the host exposes to Python.</summary>
    public Dictionary<string, Func<PyObject[], PyObject>> ExternalFunctions { get; init; } =
        new(StringComparer.Ordinal);
}

/// <summary>Convenience entry point for a session with both interpreters.</summary>
public static class Computerwelt
{
    /// <summary>
    /// Builds a shell whose <c>python</c> command runs against the same virtual filesystem.
    /// </summary>
    public static BashBuilder WithPython(this BashBuilder builder, PythonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .WithBuiltin(new PythonBuiltin("python", options))
            .WithBuiltin(new PythonBuiltin("python3", options));
    }
}
