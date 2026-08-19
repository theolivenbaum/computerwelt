using Bashkit.Builtins;
using Bashkit.Interpreter;
using Bashkit.Parsing;

namespace Bashkit;

/// <summary>
/// A sandboxed bash session.
/// </summary>
/// <remarks>
/// <para>
/// One instance is one isolated world: its own filesystem, variables and limits. Two
/// instances share nothing mutable, so a host can run scripts for different tenants
/// concurrently without any further arrangement.
/// </para>
/// <para>
/// State persists between <see cref="ExecAsync(string, CancellationToken)"/> calls, as it
/// would in a terminal session: variables set by one call are visible to the next.
/// Limits, however, are per-call.
/// </para>
/// </remarks>
public sealed class Bash
{
    private readonly IReadOnlyDictionary<string, IBuiltin> _builtins;

    internal Bash(
        ShellState state,
        IFileSystem fileSystem,
        ExecutionLimits limits,
        IReadOnlyDictionary<string, IBuiltin> builtins)
    {
        State = state;
        FileSystem = fileSystem;
        Limits = limits;
        _builtins = builtins;
    }

    /// <summary>Creates a session with the default configuration.</summary>
    public static Bash Create() => new BashBuilder().Build();

    /// <summary>Starts configuring a session.</summary>
    public static BashBuilder CreateBuilder() => new();

    /// <summary>The session's shell state.</summary>
    public ShellState State { get; }

    /// <summary>The session's virtual filesystem.</summary>
    public IFileSystem FileSystem { get; }

    /// <summary>The per-execution limits.</summary>
    public ExecutionLimits Limits { get; }

    /// <summary>The current working directory.</summary>
    public VPath WorkingDirectory => State.WorkingDirectory;

    /// <summary>The names of every registered command.</summary>
    public IReadOnlyCollection<string> BuiltinNames => (IReadOnlyCollection<string>)_builtins.Keys;

    /// <summary>Runs <paramref name="script"/> and returns its result.</summary>
    public ValueTask<ExecResult> ExecAsync(string script, CancellationToken cancellationToken = default) =>
        ExecAsync(script, new ExecOptions(), cancellationToken);

    /// <summary>Runs <paramref name="script"/> with per-call options.</summary>
    public async ValueTask<ExecResult> ExecAsync(string script, ExecOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        script ??= string.Empty;

        if (script.Length > Limits.MaxInputBytes)
        {
            throw new LimitExceededException("max_input_bytes", Limits.MaxInputBytes);
        }

        var budget = new ExecutionBudget(Limits, cancellationToken);

        var savedPositional = State.Positional;
        var savedName = State.ScriptName;

        if (options.PositionalParameters is { } positional)
        {
            State.Positional = [.. positional];
        }

        if (options.ScriptName is { } name)
        {
            State.ScriptName = name;
        }

        try
        {
            Script parsed;
            try
            {
                parsed = Parser.Parse(script, budget);
            }
            catch (ParseException e)
            {
                return ExecResult.Error($"bash: {e.Message}\n", ExitCodes.Usage);
            }

            if (State.Options.NoExec)
            {
                return ExecResult.Success;
            }

            var interpreter = new Interpreter.Interpreter(State, FileSystem, budget, _builtins);

            try
            {
                var result = await interpreter.RunAsync(parsed, options.Stdin, cancellationToken);
                State.LastExitCode = result.ExitCode;

                // The EXIT trap runs once the script is finished, whatever ended it, and
                // its output is appended to the script's own.
                var atExit = await interpreter.RunTrapAsync("EXIT", cancellationToken);

                if (!atExit.Stdout.IsEmpty || !atExit.Stderr.IsEmpty)
                {
                    result = result with
                    {
                        Stdout = StreamData.Concat(result.Stdout, atExit.Stdout),
                        Stderr = StreamData.Concat(result.Stderr, atExit.Stderr),
                    };
                }

                return result;
            }
            catch (LimitExceededException e)
            {
                return ExecResult.Error($"bash: {e.Message}\n", ExitCodes.Failure);
            }
            catch (BashkitException e) when (e.Kind is BashkitErrorKind.Timeout or BashkitErrorKind.Cancelled)
            {
                return ExecResult.Error($"bash: {e.Message}\n", e.ExitCode);
            }
        }
        finally
        {
            State.Positional = savedPositional;
            State.ScriptName = savedName;
        }
    }

    /// <summary>Sets an exported environment variable.</summary>
    public void SetEnvironmentVariable(string name, string value)
    {
        State.Set(name, value);
        State.GetOrCreate(name).Attributes |= VariableAttributes.Exported;
    }

    /// <summary>Reads a shell variable, or <see langword="null"/> when unset.</summary>
    public string? GetVariable(string name) => State.Get(name);
}

/// <summary>Per-call execution options.</summary>
public sealed record ExecOptions
{
    /// <summary>Standard input for the script.</summary>
    public StreamData? Stdin { get; init; }

    /// <summary>Positional parameters <c>$1</c> onwards for this call.</summary>
    public IReadOnlyList<string>? PositionalParameters { get; init; }

    /// <summary><c>$0</c> for this call.</summary>
    public string? ScriptName { get; init; }
}
