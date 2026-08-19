using System.Globalization;

namespace Bashkit.Builtins;

/// <summary><c>true</c> — succeeds.</summary>
public sealed class TrueBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "true";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ExecResult.Success);
}

/// <summary><c>false</c> — fails.</summary>
public sealed class FalseBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "false";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ExecResult.FromExitCode(1));
}

/// <summary><c>:</c> — the null command, which succeeds and does nothing.</summary>
public sealed class ColonBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => ":";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ExecResult.Success);
}

/// <summary><c>exit</c> — terminates the script.</summary>
public sealed class ExitBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "exit";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        // With no argument, `exit` reports the status of the last command.
        var code = context.Arguments.Count > 0
            ? ParseStatus(context.Arguments[0])
            : context.State.LastExitCode;

        return ValueTask.FromResult(new ExecResult
        {
            ExitCode = code,
            ControlFlow = ControlFlow.Exit(code),
        });
    }

    /// <summary>Exit statuses wrap to a byte, so <c>exit 256</c> is <c>exit 0</c>.</summary>
    internal static int ParseStatus(string text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? ((value % 256) + 256) % 256
            : ExitCodes.Usage;
}

/// <summary><c>return</c> — returns from a function or sourced script.</summary>
public sealed class ReturnBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "return";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var code = context.Arguments.Count > 0
            ? ExitBuiltin.ParseStatus(context.Arguments[0])
            : context.State.LastExitCode;

        return ValueTask.FromResult(new ExecResult
        {
            ExitCode = code,
            ControlFlow = ControlFlow.Return(code),
        });
    }
}

/// <summary><c>break</c> — leaves an enclosing loop.</summary>
public sealed class BreakBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "break";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var levels = ParseLevels(context.Arguments);
        return ValueTask.FromResult(new ExecResult { ControlFlow = ControlFlow.Break(levels) });
    }

    internal static int ParseLevels(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && int.TryParse(arguments[0], CultureInfo.InvariantCulture, out var levels) && levels > 0
            ? levels
            : 1;
}

/// <summary><c>continue</c> — starts the next iteration of an enclosing loop.</summary>
public sealed class ContinueBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "continue";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var levels = BreakBuiltin.ParseLevels(context.Arguments);
        return ValueTask.FromResult(new ExecResult { ControlFlow = ControlFlow.Continue(levels) });
    }
}

/// <summary><c>caller</c> — reports the call frame a function was invoked from.</summary>
/// <remarks>
/// The frame numbering counts outwards from the current function: frame 0 is whoever called
/// it. Outside a function there is no frame at all, which is why <c>caller</c> then fails
/// rather than printing the top level.
/// </remarks>
public sealed class CallerBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "caller";

    /// <inheritdoc />
    public string? LlmHint => "caller: Prints the line, function and source of an enclosing call frame.";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var stack = context.State.CallStack;

        if (stack.Count == 0)
        {
            return ValueTask.FromResult(ExecResult.FromExitCode(ExitCodes.Failure));
        }

        var frame = context.Arguments.Count > 0
            && int.TryParse(context.Arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;

        var index = stack.Count - 2 - frame;

        if (index < -1)
        {
            return ValueTask.FromResult(ExecResult.FromExitCode(ExitCodes.Failure));
        }

        // Below the outermost function is the script itself, which bash calls `main`.
        var caller = index >= 0 ? stack[index] : "main";
        var line = context.State.CurrentLine.ToString(CultureInfo.InvariantCulture);

        // With no script file — `bash -c` — the source of every frame is bash's own `main`.
        var source = context.State.ScriptName is "bash" or "" ? "main" : context.State.ScriptName;

        return ValueTask.FromResult(ExecResult.Ok($"{line} {caller} {source}\n"));
    }
}
