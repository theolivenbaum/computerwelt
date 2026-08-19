using System.Globalization;
using System.Text;
using Bashkit.Interpreter;

namespace Bashkit.Builtins;

/// <summary>The shell capabilities exposed to builtins that need them.</summary>
/// <param name="RunFragment">Runs a script fragment in the current shell.</param>
/// <param name="RunCommand">Runs one already-expanded command line in the current shell.</param>
/// <param name="IsBuiltin">Tests whether a name resolves to a registered command.</param>
/// <param name="BuiltinNames">Every registered command name.</param>
public sealed record ShellHooks(
    Func<string, StreamData?, CancellationToken, ValueTask<ExecResult>> RunFragment,
    Func<IReadOnlyList<string>, StreamData?, CancellationToken, ValueTask<ExecResult>> RunCommand,
    Func<string, bool> IsBuiltin,
    Func<IEnumerable<string>> BuiltinNames);

/// <summary><c>source</c> and <c>.</c> — run a script from the filesystem in this shell.</summary>
public sealed class SourceBuiltin : IBuiltin
{
    /// <summary>Creates the builtin under <paramref name="name"/>, either <c>source</c> or <c>.</c>.</summary>
    public SourceBuiltin(string name = "source") => Name = name;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (context.Arguments.Count == 0)
        {
            return ExecResult.Usage(Name, "filename argument required");
        }

        var target = context.Arguments[0];
        var candidates = new List<VPath> { context.ResolvePath(target) };

        // A bare name is looked up on PATH, exactly as bash does for `source`.
        if (!target.Contains('/', StringComparison.Ordinal))
        {
            foreach (var directory in (context.State.Get("PATH") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(VPath.Parse(directory).Join(target));
            }
        }

        foreach (var candidate in candidates)
        {
            byte[] bytes;
            try
            {
                bytes = await context.FileSystem.ReadFileAsync(candidate, cancellationToken);
            }
            catch (FileSystemException)
            {
                continue;
            }

            var script = Encoding.UTF8.GetString(bytes);
            var savedPositional = context.State.Positional;

            // Extra arguments become the sourced script's positional parameters.
            if (context.Arguments.Count > 1)
            {
                context.State.Positional = [.. context.Arguments.Skip(1)];
            }

            try
            {
                var result = await context.Hooks.RunFragment(script, context.Stdin, cancellationToken);

                // `return` inside a sourced file returns from the file, not the caller.
                return result.ControlFlow.Kind == ControlFlowKind.Return
                    ? result with { ControlFlow = ControlFlow.None, ExitCode = result.ControlFlow.Level }
                    : result;
            }
            finally
            {
                context.State.Positional = savedPositional;
            }
        }

        return ExecResult.Error($"bash: {Name}: {target}: No such file or directory\n", ExitCodes.Failure);
    }
}

/// <summary><c>command</c> — runs a command bypassing functions and aliases.</summary>
public sealed class CommandBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "command";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var index = 0;
        var describe = false;
        var verbose = false;

        while (index < context.Arguments.Count && context.Arguments[index].StartsWith('-'))
        {
            switch (context.Arguments[index])
            {
                case "-v": describe = true; break;
                case "-V": verbose = true; break;
                case "-p": break;
                case "--": index++; goto done;
                default: goto done;
            }

            index++;
        }

    done:
        var rest = context.Arguments.Skip(index).ToList();

        if (rest.Count == 0)
        {
            return ExecResult.Success;
        }

        if (describe || verbose)
        {
            var builder = new StringBuilder();
            var allFound = true;

            foreach (var name in rest)
            {
                if (context.State.Functions.ContainsKey(name))
                {
                    builder.Append(verbose ? $"{name} is a function\n" : name + "\n");
                }
                else if (context.Hooks.IsBuiltin(name))
                {
                    builder.Append(verbose ? $"{name} is a shell builtin\n" : name + "\n");
                }
                else
                {
                    allFound = false;
                }
            }

            return new ExecResult
            {
                Stdout = StreamData.FromText(builder.ToString()),
                ExitCode = allFound ? 0 : 1,
            };
        }

        // Functions are bypassed: `command foo` runs the builtin even when `foo()` exists.
        if (!context.Hooks.IsBuiltin(rest[0]))
        {
            return ExecResult.Error($"bash: command: {rest[0]}: command not found\n", ExitCodes.NotFound);
        }

        return await context.Hooks.RunCommand(rest, context.Stdin, cancellationToken);
    }
}

/// <summary><c>which</c> — reports where a command would be found.</summary>
public sealed class WhichBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "which";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);

        while (cursor.NextOption() is { } option)
        {
            if (option is "-a" or "--all" or "-s")
            {
                continue;
            }

            return ValueTask.FromResult(ExecResult.Usage("which", $"invalid option -- '{option.TrimStart('-')}'"));
        }

        var builder = new StringBuilder();
        var allFound = true;

        foreach (var name in cursor.Operands)
        {
            // There are no real executables, so a registered builtin reports the
            // conventional location a script would expect to see.
            if (context.Hooks.IsBuiltin(name) || context.State.Functions.ContainsKey(name))
            {
                builder.Append("/usr/bin/").Append(name).Append('\n');
            }
            else
            {
                allFound = false;
            }
        }

        return ValueTask.FromResult(new ExecResult
        {
            Stdout = StreamData.FromText(builder.ToString()),
            ExitCode = allFound ? 0 : 1,
        });
    }
}

/// <summary><c>hash</c> — the command-location cache, which this shell does not need.</summary>
public sealed class HashBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "hash";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default) =>
        // Commands are resolved from an in-process registry, so there is nothing to cache
        // and nothing to forget. Succeeding silently is the honest no-op.
        ValueTask.FromResult(ExecResult.Success);
}

/// <summary><c>getopts</c> — parses positional parameters as options.</summary>
public sealed class GetoptsBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "getopts";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (context.Arguments.Count < 2)
        {
            return ValueTask.FromResult(ExecResult.Usage("getopts", "usage: getopts optstring name [arg ...]"));
        }

        var optionString = context.Arguments[0];
        var variableName = context.Arguments[1];
        var arguments = context.Arguments.Count > 2
            ? context.Arguments.Skip(2).ToList()
            : context.State.Positional;

        var silent = optionString.StartsWith(':');
        var spec = silent ? optionString[1..] : optionString;

        // OPTIND is 1-based and persists across calls; that is how the loop advances.
        var optind = int.TryParse(context.State.Get("OPTIND"), CultureInfo.InvariantCulture, out var stored) ? stored : 1;
        var charIndex = int.TryParse(context.State.Get("_GETOPTS_CHAR"), CultureInfo.InvariantCulture, out var chars) ? chars : 1;

        if (optind - 1 >= arguments.Count)
        {
            return Finish(context, variableName, "?", optind, 1, 1);
        }

        var current = arguments[optind - 1];

        if (current.Length < 2 || current[0] != '-' || current == "-")
        {
            return Finish(context, variableName, "?", optind, 1, 1);
        }

        if (current == "--")
        {
            return Finish(context, variableName, "?", optind + 1, 1, 1);
        }

        var flag = current[charIndex];
        var position = spec.IndexOf(flag, StringComparison.Ordinal);

        if (position < 0)
        {
            var nextChar = charIndex + 1;
            var nextInd = optind;
            if (nextChar >= current.Length)
            {
                nextChar = 1;
                nextInd++;
            }

            context.State.Set("OPTARG", silent ? flag.ToString() : string.Empty);
            var message = silent ? string.Empty : $"bash: illegal option -- {flag}\n";

            context.State.Set(variableName, "?");
            context.State.Set("OPTIND", nextInd.ToString(CultureInfo.InvariantCulture));
            context.State.Set("_GETOPTS_CHAR", nextChar.ToString(CultureInfo.InvariantCulture));

            return ValueTask.FromResult(new ExecResult
            {
                Stderr = StreamData.FromText(message),
                ExitCode = 0,
            });
        }

        var wantsArgument = position + 1 < spec.Length && spec[position + 1] == ':';

        if (!wantsArgument)
        {
            var nextChar = charIndex + 1;
            var nextInd = optind;
            if (nextChar >= current.Length)
            {
                nextChar = 1;
                nextInd++;
            }

            context.State.Unset("OPTARG");
            return Finish(context, variableName, flag.ToString(), nextInd, nextChar, 0);
        }

        // The value is the rest of this argument, or the next argument entirely.
        if (charIndex + 1 < current.Length)
        {
            context.State.Set("OPTARG", current[(charIndex + 1)..]);
            return Finish(context, variableName, flag.ToString(), optind + 1, 1, 0);
        }

        if (optind >= arguments.Count)
        {
            context.State.Set(variableName, silent ? ":" : "?");
            context.State.Set("OPTARG", silent ? flag.ToString() : string.Empty);
            context.State.Set("OPTIND", (optind + 1).ToString(CultureInfo.InvariantCulture));
            context.State.Set("_GETOPTS_CHAR", "1");

            return ValueTask.FromResult(new ExecResult
            {
                Stderr = silent ? StreamData.Empty : StreamData.FromText($"bash: option requires an argument -- {flag}\n"),
                ExitCode = 0,
            });
        }

        context.State.Set("OPTARG", arguments[optind]);
        return Finish(context, variableName, flag.ToString(), optind + 2, 1, 0);
    }

    private static ValueTask<ExecResult> Finish(
        BuiltinContext context, string variableName, string value, int optind, int charIndex, int exitCode)
    {
        context.State.Set(variableName, value);
        context.State.Set("OPTIND", optind.ToString(CultureInfo.InvariantCulture));
        context.State.Set("_GETOPTS_CHAR", charIndex.ToString(CultureInfo.InvariantCulture));
        return ValueTask.FromResult(ExecResult.FromExitCode(exitCode));
    }
}

/// <summary><c>let</c> — evaluates arithmetic expressions.</summary>
public sealed class LetBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "let";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (context.Arguments.Count == 0)
        {
            return ValueTask.FromResult(ExecResult.Usage("let", "expression expected", ExitCodes.Failure));
        }

        long last = 0;

        try
        {
            foreach (var expression in context.Arguments)
            {
                last = ArithmeticEvaluator.Evaluate(context.State, expression);
            }
        }
        catch (ShellArithmeticException e)
        {
            return ValueTask.FromResult(ExecResult.Error($"bash: let: {e.Message}\n", ExitCodes.Failure));
        }

        // Like `(( ))`, `let` succeeds when the last expression is non-zero.
        return ValueTask.FromResult(ExecResult.FromExitCode(last != 0 ? 0 : 1));
    }
}

/// <summary><c>trap</c> — records handlers for signals and shell events.</summary>
public sealed class TrapBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "trap";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (context.Arguments.Count == 0 || context.Arguments[0] == "-p")
        {
            var builder = new StringBuilder();
            foreach (var (signal, handler) in context.State.Traps.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                builder.Append("trap -- '").Append(handler).Append("' ").Append(signal).Append('\n');
            }

            return ValueTask.FromResult(ExecResult.Ok(builder.ToString()));
        }

        if (context.Arguments[0] == "-l")
        {
            return ValueTask.FromResult(ExecResult.Ok(
                " 1) SIGHUP\t 2) SIGINT\t 3) SIGQUIT\t 9) SIGKILL\t15) SIGTERM\n"));
        }

        var action = context.Arguments[0];
        var signals = context.Arguments.Skip(1).ToList();

        foreach (var signal in signals)
        {
            var normalized = Normalize(signal);

            // `-` restores the default, which here means forgetting the handler.
            if (action is "-")
            {
                context.State.Traps.Remove(normalized);
                continue;
            }

            context.State.Traps[normalized] = action;
        }

        return ValueTask.FromResult(ExecResult.Success);
    }

    private static string Normalize(string signal)
    {
        var upper = signal.ToUpperInvariant();
        return upper.StartsWith("SIG", StringComparison.Ordinal) ? upper[3..] : upper;
    }
}
