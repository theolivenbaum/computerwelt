using System.Globalization;
using System.Text.RegularExpressions;
using Bashkit.Parsing;

namespace Bashkit.Interpreter;

/// <summary>
/// Evaluates <c>[[ ... ]]</c> conditional expressions.
/// </summary>
/// <remarks>
/// The differences from the <c>test</c> builtin are the reason this is separate code:
/// operands are not word-split or globbed, the right-hand side of <c>==</c> is a pattern,
/// and <c>=~</c> matches an ERE and populates <c>BASH_REMATCH</c>.
/// </remarks>
public static class ConditionalEvaluator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Evaluates <paramref name="expression"/> and returns whether it is true.</summary>
    public static async ValueTask<bool> EvaluateAsync(
        Interpreter interpreter,
        ConditionalExpression expression,
        CancellationToken cancellationToken)
    {
        switch (expression)
        {
            case ConditionalExpression.Not not:
                return !await EvaluateAsync(interpreter, not.Operand, cancellationToken);

            case ConditionalExpression.And and:
                return await EvaluateAsync(interpreter, and.Left, cancellationToken)
                    && await EvaluateAsync(interpreter, and.Right, cancellationToken);

            case ConditionalExpression.Or or:
                return await EvaluateAsync(interpreter, or.Left, cancellationToken)
                    || await EvaluateAsync(interpreter, or.Right, cancellationToken);

            case ConditionalExpression.Value value:
                return (await interpreter.Expander.ExpandToStringAsync(value.Operand, cancellationToken)).Length > 0;

            case ConditionalExpression.Unary unary:
            {
                var operand = await interpreter.Expander.ExpandToStringAsync(unary.Operand, cancellationToken);
                return await EvaluateUnaryAsync(interpreter, unary.Operator, operand, cancellationToken);
            }

            case ConditionalExpression.Binary binary:
                return await EvaluateBinaryAsync(interpreter, binary, cancellationToken);

            default:
                return false;
        }
    }

    /// <summary>Evaluates a unary test such as <c>-f</c> or <c>-z</c>.</summary>
    public static async ValueTask<bool> EvaluateUnaryAsync(
        Interpreter interpreter,
        string op,
        string operand,
        CancellationToken cancellationToken)
    {
        switch (op)
        {
            case "-z":
                return operand.Length == 0;
            case "-n":
                return operand.Length > 0;
            case "-v":
                return interpreter.State.IsSetReference(operand);

            // There are no real descriptors, so a host decides which are terminals by
            // setting `_TTY_<fd>`; unset means not a terminal, which is the safe default.
            case "-t":
                return interpreter.State.Get("_TTY_" + operand) == "1";
            case "-o":
                return interpreter.State.Options.GetByName(operand) ?? false;
        }

        var path = VPath.Resolve(interpreter.State.WorkingDirectory, operand);
        var metadata = await TryStatAsync(interpreter.FileSystem, path, followLinks: op != "-h" && op != "-L", cancellationToken);

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
            "-b" or "-c" or "-S" => false,
            "-g" or "-u" or "-k" => false,
            "-G" or "-O" => metadata is not null,
            "-N" => metadata is not null,
            _ => false,
        };
    }

    private static async ValueTask<bool> EvaluateBinaryAsync(
        Interpreter interpreter,
        ConditionalExpression.Binary binary,
        CancellationToken cancellationToken)
    {
        var left = await interpreter.Expander.ExpandToStringAsync(binary.Left, cancellationToken);

        switch (binary.Operator)
        {
            case "=" or "==" or "!=":
            {
                // The right operand is a pattern, not a literal — this is the headline
                // difference between `[[ ]]` and `[ ]`.
                var pattern = await interpreter.Expander.ExpandToPatternAsync(binary.Right, cancellationToken);
                var matched = PatternMatcher.IsMatch(
                    left,
                    pattern,
                    interpreter.State.Options.NoCaseMatch,
                    interpreter.State.Options.ExtGlob);

                return binary.Operator == "!=" ? !matched : matched;
            }

            case "=~":
            {
                var pattern = await interpreter.Expander.ExpandToPatternAsync(binary.Right, cancellationToken);
                return MatchRegex(interpreter.State, left, UnescapePattern(pattern));
            }
        }

        var right = await interpreter.Expander.ExpandToStringAsync(binary.Right, cancellationToken);

        switch (binary.Operator)
        {
            case "<":
                return string.CompareOrdinal(left, right) < 0;
            case ">":
                return string.CompareOrdinal(left, right) > 0;
        }

        if (binary.Operator is "-eq" or "-ne" or "-lt" or "-le" or "-gt" or "-ge")
        {
            var a = ParseNumber(interpreter.State, left);
            var b = ParseNumber(interpreter.State, right);

            return binary.Operator switch
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

        if (binary.Operator is "-nt" or "-ot" or "-ef")
        {
            var leftPath = VPath.Resolve(interpreter.State.WorkingDirectory, left);
            var rightPath = VPath.Resolve(interpreter.State.WorkingDirectory, right);
            var leftMeta = await TryStatAsync(interpreter.FileSystem, leftPath, followLinks: true, cancellationToken);
            var rightMeta = await TryStatAsync(interpreter.FileSystem, rightPath, followLinks: true, cancellationToken);

            return binary.Operator switch
            {
                "-nt" => leftMeta is not null && (rightMeta is null || leftMeta.ModifiedAt > rightMeta.ModifiedAt),
                "-ot" => rightMeta is not null && (leftMeta is null || leftMeta.ModifiedAt < rightMeta.ModifiedAt),
                "-ef" => leftMeta is not null && rightMeta is not null && leftPath == rightPath,
                _ => false,
            };
        }

        return false;
    }

    private static bool MatchRegex(ShellState state, string subject, string pattern)
    {
        try
        {
            var options = state.Options.NoCaseMatch ? RegexOptions.IgnoreCase : RegexOptions.None;
            var match = Regex.Match(subject, pattern, options, RegexTimeout);

            // `BASH_REMATCH[0]` is the whole match and the rest are the capture groups.
            var rematch = state.GetOrCreate("BASH_REMATCH");
            if (match.Success)
            {
                rematch.SetArray(match.Groups.Values.Select(static g => g.Success ? g.Value : string.Empty));
            }
            else
            {
                rematch.SetArray([]);
            }

            return match.Success;
        }
        catch (ArgumentException)
        {
            // An invalid ERE is a runtime error in bash too; treat it as no match.
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// The pattern expander escapes glob metacharacters in quoted text. Regex operands must
    /// have that escaping removed, since a quoted <c>[</c> is a literal in both syntaxes.
    /// </summary>
    private static string UnescapePattern(string pattern)
    {
        if (!pattern.Contains('\\', StringComparison.Ordinal))
        {
            return pattern;
        }

        return pattern;
    }

    private static long ParseNumber(ShellState state, string text)
    {
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return ArithmeticEvaluator.Evaluate(state, text);
    }

    private static async ValueTask<FileMetadata?> TryStatAsync(
        IFileSystem fileSystem,
        VPath path,
        bool followLinks,
        CancellationToken cancellationToken)
    {
        try
        {
            return followLinks
                ? await fileSystem.StatAsync(path, cancellationToken)
                : await fileSystem.StatLinkAsync(path, cancellationToken);
        }
        catch (FileSystemException)
        {
            return null;
        }
    }
}
