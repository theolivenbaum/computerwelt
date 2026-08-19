using System.Text;

namespace Bashkit.Builtins;

/// <summary>
/// <c>chown</c> and <c>chgrp</c>.
/// </summary>
/// <remarks>
/// The virtual filesystem has one user and no permission model built on ownership, so these
/// validate their arguments and succeed without changing anything. Validating is the part
/// worth keeping: a script that misspells a path still learns about it here rather than
/// silently continuing.
/// </remarks>
public sealed class ChownBuiltin : IBuiltin
{
    /// <summary>Creates the builtin under <paramref name="name"/>, either <c>chown</c> or <c>chgrp</c>.</summary>
    public ChownBuiltin(string name = "chown") => Name = name;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string? LlmHint => $"{Name}: Accepted and validated; the virtual filesystem has a single owner.";

    /// <inheritdoc />
    public string? Help =>
        $"Usage: {Name} [OPTION]... OWNER[:GROUP] FILE...\nChange file ownership.\n";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var operands = new List<string>();

        foreach (var argument in context.Arguments)
        {
            if (argument == "--help")
            {
                return ExecResult.Ok(Help!);
            }

            if (argument.Length > 1 && argument[0] == '-')
            {
                continue;
            }

            operands.Add(argument);
        }

        if (operands.Count < 2)
        {
            return ExecResult.Error($"{Name}: missing operand\n", ExitCodes.Failure);
        }

        var errors = new StringBuilder();

        foreach (var operand in operands.Skip(1))
        {
            if (!await context.FileSystem.ExistsAsync(context.ResolvePath(operand), cancellationToken))
            {
                errors.Append($"{Name}: cannot access '{operand}': No such file or directory\n");
            }
        }

        return errors.Length == 0
            ? ExecResult.Success
            : ExecResult.Error(errors.ToString(), ExitCodes.Failure);
    }
}

/// <summary>
/// <c>kill</c> — signal a process.
/// </summary>
/// <remarks>
/// There are no processes to signal, so the command exists for <c>kill -l</c> and for
/// scripts that probe with <c>kill -0</c>. Sending a real signal is not something a
/// sandboxed shell can do, and pretending otherwise would be worse than the no-op.
/// </remarks>
public sealed class KillBuiltin : IBuiltin
{
    private static readonly string[] Signals =
    [
        "HUP", "INT", "QUIT", "ILL", "TRAP", "ABRT", "BUS", "FPE", "KILL", "USR1",
        "SEGV", "USR2", "PIPE", "ALRM", "TERM", "STKFLT", "CHLD", "CONT", "STOP", "TSTP",
        "TTIN", "TTOU", "URG", "XCPU", "XFSZ", "VTALRM", "PROF", "WINCH", "IO", "PWR", "SYS",
    ];

    /// <inheritdoc />
    public string Name => "kill";

    /// <inheritdoc />
    public string? LlmHint => "kill: List signals with -l. There are no processes to signal here.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: kill [-s SIGNAL | -SIGNAL] PID...
               kill -l [SIGNAL]
        Send a signal to a process.
        """;

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (context.Arguments.Count == 0)
        {
            return ValueTask.FromResult(ExecResult.Usage(Name, "usage: kill [-s sigspec | -n signum | -sigspec] pid"));
        }

        if (context.Arguments[0] is "-l" or "--list")
        {
            var output = new StringBuilder();

            for (var i = 0; i < Signals.Length; i++)
            {
                output.Append(i + 1).Append(") SIG").Append(Signals[i]).Append(i % 4 == 3 ? "\n" : "\t");
            }

            if (output.Length > 0 && output[^1] == '\t')
            {
                output[^1] = '\n';
            }

            return ValueTask.FromResult(ExecResult.Ok(output.ToString()));
        }

        if (context.Arguments[0] == "--help")
        {
            return ValueTask.FromResult(ExecResult.Ok(Help + "\n"));
        }

        return ValueTask.FromResult(ExecResult.Success);
    }
}
