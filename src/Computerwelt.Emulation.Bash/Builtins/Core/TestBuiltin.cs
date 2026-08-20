using System.Globalization;
using Computerwelt.Emulation.Bash.Interpreter;

namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// <c>test</c> and <c>[</c> — evaluates a conditional expression.
/// </summary>
/// <remarks>
/// Unlike <c>[[ ]]</c>, this is an ordinary command: its operands have already been
/// word-split and globbed by the time it sees them, and its <c>=</c> compares strings
/// literally rather than as patterns. The POSIX grammar is defined by operand <i>count</i>
/// — one, two, three or four arguments each have their own rule — which is why the
/// implementation dispatches on length before looking at content.
/// </remarks>
public sealed class TestBuiltin : IBuiltin
{
    /// <summary>Creates the builtin under <paramref name="name"/>, either <c>test</c> or <c>[</c>.</summary>
    public TestBuiltin(string name = "test") => Name = name;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var arguments = context.Arguments;

        // `[` requires a closing `]`, which is otherwise just another argument.
        if (Name == "[")
        {
            if (arguments.Count == 0 || arguments[^1] != "]")
            {
                return ExecResult.Usage("[", "missing `]'");
            }

            arguments = [.. arguments.Take(arguments.Count - 1)];
        }

        try
        {
            var result = await EvaluateAsync(context, arguments, cancellationToken);
            return ExecResult.FromExitCode(result ? 0 : 1);
        }
        catch (BashkitException e)
        {
            return ExecResult.Usage(Name, e.Message);
        }
    }

    private static async ValueTask<bool> EvaluateAsync(BuiltinContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        switch (arguments.Count)
        {
            case 0:
                return false;

            case 1:
                return arguments[0].Length > 0;

            case 2:
                if (arguments[0] == "!")
                {
                    return arguments[1].Length == 0;
                }

                return IsUnaryOperator(arguments[0])
                    && await EvaluateUnaryAsync(context, arguments[0], arguments[1], cancellationToken);

            case 3:
                if (IsBinaryOperator(arguments[1]))
                {
                    return await EvaluateBinaryAsync(context, arguments[0], arguments[1], arguments[2], cancellationToken);
                }

                if (arguments[0] == "!")
                {
                    return !await EvaluateAsync(context, [arguments[1], arguments[2]], cancellationToken);
                }

                if (arguments[0] == "(" && arguments[2] == ")")
                {
                    return await EvaluateAsync(context, [arguments[1]], cancellationToken);
                }

                return arguments[0].Length > 0;

            case 4:
                if (arguments[0] == "!")
                {
                    return !await EvaluateAsync(context, [.. arguments.Skip(1)], cancellationToken);
                }

                break;
        }

        // Longer expressions are joined by -a / -o, which bind less tightly than the
        // primaries and are left-associative.
        return await EvaluateListAsync(context, arguments, cancellationToken);
    }

    private static async ValueTask<bool> EvaluateListAsync(BuiltinContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] != "-o")
            {
                continue;
            }

            return await EvaluateAsync(context, [.. arguments.Take(i)], cancellationToken)
                || await EvaluateAsync(context, [.. arguments.Skip(i + 1)], cancellationToken);
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] != "-a")
            {
                continue;
            }

            return await EvaluateAsync(context, [.. arguments.Take(i)], cancellationToken)
                && await EvaluateAsync(context, [.. arguments.Skip(i + 1)], cancellationToken);
        }

        return arguments.Count > 0 && arguments[0].Length > 0;
    }

    private static async ValueTask<bool> EvaluateUnaryAsync(BuiltinContext context, string op, string operand, CancellationToken cancellationToken)
    {
        switch (op)
        {
            case "-z":
                return operand.Length == 0;
            case "-n":
                return operand.Length > 0;
            case "-v":
                return context.State.IsSetReference(operand);

            // There are no real descriptors, so a host decides which are terminals by
            // setting `_TTY_<fd>`; unset means not a terminal, which is the safe default.
            case "-t":
                return context.State.Get("_TTY_" + operand) == "1";
            case "-o":
                return context.State.Options.GetByName(operand) ?? false;
        }

        var path = context.ResolvePath(operand);
        var metadata = await TryStatAsync(context, path, followLinks: op is not ("-h" or "-L"), cancellationToken);

        return op switch
        {
            "-e" => metadata is not null,
            "-f" => metadata is { IsFile: true },
            "-d" => metadata is { IsDirectory: true },
            "-h" or "-L" => metadata is { IsSymlink: true },
            "-p" => metadata is { IsFifo: true },
            "-s" => metadata is { Size: > 0 },
            "-r" => metadata is not null && (metadata.Mode & 0b100_000_000) != 0,
            "-w" => metadata is not null && (metadata.Mode & 0b010_000_000) != 0,
            "-x" => metadata is not null && (metadata.Mode & 0b001_000_000) != 0,
            "-G" or "-O" or "-N" => metadata is not null,
            _ => false,
        };
    }

    private static async ValueTask<bool> EvaluateBinaryAsync(BuiltinContext context, string left, string op, string right, CancellationToken cancellationToken)
    {
        switch (op)
        {
            case "=" or "==":
                return string.Equals(left, right, StringComparison.Ordinal);
            case "!=":
                return !string.Equals(left, right, StringComparison.Ordinal);
            case "<":
                return string.CompareOrdinal(left, right) < 0;
            case ">":
                return string.CompareOrdinal(left, right) > 0;
        }

        if (op is "-eq" or "-ne" or "-lt" or "-le" or "-gt" or "-ge")
        {
            if (!TryParseInteger(left, out var a) || !TryParseInteger(right, out var b))
            {
                throw new BashkitException(BashkitErrorKind.Internal, $"{(TryParseInteger(left, out _) ? right : left)}: integer expression expected");
            }

            return op switch
            {
                "-eq" => a == b,
                "-ne" => a != b,
                "-lt" => a < b,
                "-le" => a <= b,
                "-gt" => a > b,
                "-ge" => a >= b,
                _ => false,
            };
        }

        if (op is "-nt" or "-ot" or "-ef")
        {
            var leftPath = context.ResolvePath(left);
            var rightPath = context.ResolvePath(right);
            var leftMeta = await TryStatAsync(context, leftPath, followLinks: true, cancellationToken);
            var rightMeta = await TryStatAsync(context, rightPath, followLinks: true, cancellationToken);

            return op switch
            {
                "-nt" => leftMeta is not null && (rightMeta is null || leftMeta.ModifiedAt > rightMeta.ModifiedAt),
                "-ot" => rightMeta is not null && (leftMeta is null || leftMeta.ModifiedAt < rightMeta.ModifiedAt),
                "-ef" => leftMeta is not null && rightMeta is not null && leftPath == rightPath,
                _ => false,
            };
        }

        return false;
    }

    private static bool TryParseInteger(string text, out long value) =>
        long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    private static bool IsUnaryOperator(string text) =>
        text.Length == 2 && text[0] == '-' && "abcdefghkLnoprstuvwxzGONS".Contains(text[1], StringComparison.Ordinal);

    private static bool IsBinaryOperator(string text) =>
        text is "=" or "==" or "!=" or "<" or ">"
            or "-eq" or "-ne" or "-lt" or "-le" or "-gt" or "-ge"
            or "-nt" or "-ot" or "-ef";

    private static async ValueTask<FileMetadata?> TryStatAsync(BuiltinContext context, VPath path, bool followLinks, CancellationToken cancellationToken)
    {
        try
        {
            return followLinks
                ? await context.FileSystem.StatAsync(path, cancellationToken)
                : await context.FileSystem.StatLinkAsync(path, cancellationToken);
        }
        catch (FileSystemException)
        {
            return null;
        }
    }
}
