using System.Globalization;
using System.Text;

namespace Bashkit.Builtins;

/// <summary>Shared helpers for the line-oriented text builtins.</summary>
internal static class TextHelpers
{
    /// <summary>
    /// Splits text into lines. A trailing newline is a terminator, not a separator, so it
    /// must not produce a phantom final empty line — <c>printf 'a\n' | wc -l</c> is 1.
    /// </summary>
    public static string[] SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var trimmed = text.EndsWith('\n') ? text[..^1] : text;
        return trimmed.Split('\n');
    }

    /// <summary>Joins lines back with a trailing newline, or returns empty for no lines.</summary>
    public static string JoinLines(IEnumerable<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Reads every operand's content, or stdin when there are none.</summary>
    public static async ValueTask<List<(string Name, string Content)>> ReadInputsAsync(
        BuiltinContext context,
        IReadOnlyList<string> operands,
        string command,
        StringBuilder errors,
        CancellationToken cancellationToken)
    {
        var inputs = new List<(string, string)>();

        if (operands.Count == 0)
        {
            inputs.Add(("-", context.StdinText));
            return inputs;
        }

        foreach (var operand in operands)
        {
            if (operand == "-")
            {
                inputs.Add(("-", context.StdinText));
                continue;
            }

            try
            {
                var bytes = await context.FileSystem.ReadFileAsync(context.ResolvePath(operand), cancellationToken);
                inputs.Add((operand, Encoding.UTF8.GetString(bytes)));
            }
            catch (FileSystemException e)
            {
                errors.Append(command).Append(": ").Append(operand).Append(": ")
                    .Append(MkdirBuiltin.Describe(e)).Append('\n');
            }
        }

        return inputs;
    }
}

/// <summary><c>head</c> — prints the leading lines or bytes of its input.</summary>
public sealed class HeadBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "head";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var count = 10;
        var byBytes = false;
        var quiet = false;
        var verbose = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-n" or "--lines":
                    if (!TryParseCount(cursor.TakeValue(), out count))
                    {
                        return ExecResult.Usage("head", "invalid number of lines");
                    }

                    break;

                case "-c" or "--bytes":
                    byBytes = true;
                    if (!TryParseCount(cursor.TakeValue(), out count))
                    {
                        return ExecResult.Usage("head", "invalid number of bytes");
                    }

                    break;

                case "-q" or "--quiet" or "--silent": quiet = true; break;
                case "-v" or "--verbose": verbose = true; break;

                default:
                    // `head -5` is shorthand for `head -n 5`.
                    if (option.Length > 1 && option[1] is >= '0' and <= '9')
                    {
                        count = int.Parse(option[1..], CultureInfo.InvariantCulture);
                        break;
                    }

                    return ExecResult.Usage("head", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        var errors = new StringBuilder();
        var inputs = await TextHelpers.ReadInputsAsync(context, cursor.Operands, "head", errors, cancellationToken);
        var builder = new StringBuilder();
        var showHeaders = verbose || (inputs.Count > 1 && !quiet);
        var first = true;

        foreach (var (name, content) in inputs)
        {
            if (showHeaders)
            {
                if (!first)
                {
                    builder.Append('\n');
                }

                builder.Append("==> ").Append(name == "-" ? "standard input" : name).Append(" <==\n");
            }

            first = false;

            if (byBytes)
            {
                builder.Append(content[..Math.Min(content.Length, Math.Max(0, count))]);
                continue;
            }

            var lines = TextHelpers.SplitLines(content);
            builder.Append(TextHelpers.JoinLines(lines.Take(Math.Max(0, count))));
        }

        return errors.Length == 0
            ? ExecResult.Ok(builder.ToString())
            : new ExecResult
            {
                Stdout = StreamData.FromText(builder.ToString()),
                Stderr = StreamData.FromText(errors.ToString()),
                ExitCode = ExitCodes.Failure,
            };
    }

    internal static bool TryParseCount(string? text, out int count)
    {
        count = 10;
        if (text is null)
        {
            return false;
        }

        return int.TryParse(text.TrimStart('+'), CultureInfo.InvariantCulture, out count);
    }
}

/// <summary><c>tail</c> — prints the trailing lines or bytes of its input.</summary>
public sealed class TailBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "tail";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var count = 10;
        var fromStart = false;
        var byBytes = false;
        var quiet = false;
        var verbose = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-n" or "--lines":
                {
                    var value = cursor.TakeValue();
                    // `tail -n +3` counts from the start rather than the end.
                    fromStart = value?.StartsWith('+') == true;
                    if (!HeadBuiltin.TryParseCount(value, out count))
                    {
                        return ExecResult.Usage("tail", "invalid number of lines");
                    }

                    break;
                }

                case "-c" or "--bytes":
                {
                    byBytes = true;
                    var value = cursor.TakeValue();
                    fromStart = value?.StartsWith('+') == true;
                    if (!HeadBuiltin.TryParseCount(value, out count))
                    {
                        return ExecResult.Usage("tail", "invalid number of bytes");
                    }

                    break;
                }

                case "-q" or "--quiet" or "--silent": quiet = true; break;
                case "-v" or "--verbose": verbose = true; break;
                case "-f" or "--follow": break;

                default:
                    if (option.Length > 1 && option[1] is >= '0' and <= '9')
                    {
                        count = int.Parse(option[1..], CultureInfo.InvariantCulture);
                        break;
                    }

                    return ExecResult.Usage("tail", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        var errors = new StringBuilder();
        var inputs = await TextHelpers.ReadInputsAsync(context, cursor.Operands, "tail", errors, cancellationToken);
        var builder = new StringBuilder();
        var showHeaders = verbose || (inputs.Count > 1 && !quiet);
        var first = true;

        foreach (var (name, content) in inputs)
        {
            if (showHeaders)
            {
                if (!first)
                {
                    builder.Append('\n');
                }

                builder.Append("==> ").Append(name == "-" ? "standard input" : name).Append(" <==\n");
            }

            first = false;

            if (byBytes)
            {
                builder.Append(fromStart
                    ? content[Math.Min(content.Length, Math.Max(0, count - 1))..]
                    : content[Math.Max(0, content.Length - count)..]);
                continue;
            }

            var lines = TextHelpers.SplitLines(content);
            var selected = fromStart
                ? lines.Skip(Math.Max(0, count - 1))
                : lines.Skip(Math.Max(0, lines.Length - count));

            builder.Append(TextHelpers.JoinLines(selected));
        }

        return errors.Length == 0
            ? ExecResult.Ok(builder.ToString())
            : new ExecResult
            {
                Stdout = StreamData.FromText(builder.ToString()),
                Stderr = StreamData.FromText(errors.ToString()),
                ExitCode = ExitCodes.Failure,
            };
    }
}

/// <summary><c>wc</c> — counts lines, words, characters and bytes.</summary>
public sealed class WcBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "wc";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var lines = false;
        var words = false;
        var chars = false;
        var bytes = false;
        var maxLine = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-l" or "--lines": lines = true; break;
                case "-w" or "--words": words = true; break;
                case "-m" or "--chars": chars = true; break;
                case "-c" or "--bytes": bytes = true; break;
                case "-L" or "--max-line-length": maxLine = true; break;
                default:
                    return ExecResult.Usage("wc", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        // With no selection, wc reports lines, words and bytes in that order.
        if (!lines && !words && !chars && !bytes && !maxLine)
        {
            lines = words = bytes = true;
        }

        var errors = new StringBuilder();
        var inputs = await TextHelpers.ReadInputsAsync(context, cursor.Operands, "wc", errors, cancellationToken);
        var builder = new StringBuilder();

        long totalLines = 0, totalWords = 0, totalChars = 0, totalBytes = 0, totalMax = 0;

        foreach (var (name, content) in inputs)
        {
            var lineCount = content.Count(static c => c == '\n');
            var wordCount = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            var charCount = content.Length;
            var byteCount = Encoding.UTF8.GetByteCount(content);
            var longest = TextHelpers.SplitLines(content).Select(static l => l.Length).DefaultIfEmpty(0).Max();

            totalLines += lineCount;
            totalWords += wordCount;
            totalChars += charCount;
            totalBytes += byteCount;
            totalMax = Math.Max(totalMax, longest);

            AppendRow(builder, lines, words, chars, bytes, maxLine,
                lineCount, wordCount, charCount, byteCount, longest,
                inputs.Count > 1 || cursor.Operands.Count > 0 ? (name == "-" ? string.Empty : name) : null);
        }

        if (inputs.Count > 1)
        {
            AppendRow(builder, lines, words, chars, bytes, maxLine,
                totalLines, totalWords, totalChars, totalBytes, totalMax, "total");
        }

        return errors.Length == 0
            ? ExecResult.Ok(builder.ToString())
            : new ExecResult
            {
                Stdout = StreamData.FromText(builder.ToString()),
                Stderr = StreamData.FromText(errors.ToString()),
                ExitCode = ExitCodes.Failure,
            };
    }

    private static void AppendRow(
        StringBuilder builder,
        bool lines, bool words, bool chars, bool bytes, bool maxLine,
        long lineCount, long wordCount, long charCount, long byteCount, long longest,
        string? label)
    {
        var fields = new List<long>();

        if (lines)
        {
            fields.Add(lineCount);
        }

        if (words)
        {
            fields.Add(wordCount);
        }

        if (chars)
        {
            fields.Add(charCount);
        }

        if (bytes)
        {
            fields.Add(byteCount);
        }

        if (maxLine)
        {
            fields.Add(longest);
        }

        // A single count with no filename is printed bare; otherwise fields are padded to
        // width 7, which is what coreutils does for small inputs.
        if (fields.Count == 1 && string.IsNullOrEmpty(label))
        {
            builder.Append(fields[0].ToString(CultureInfo.InvariantCulture)).Append('\n');
            return;
        }

        builder.Append(string.Join(' ', fields.Select(static f => f.ToString(CultureInfo.InvariantCulture).PadLeft(7))));

        if (!string.IsNullOrEmpty(label))
        {
            builder.Append(' ').Append(label);
        }

        builder.Append('\n');
    }
}

/// <summary><c>seq</c> — prints a sequence of numbers.</summary>
public sealed class SeqBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "seq";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var separator = "\n";
        var equalWidth = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-s" or "--separator": separator = cursor.TakeValue() ?? "\n"; break;
                case "-w" or "--equal-width": equalWidth = true; break;
                case "-f" or "--format": cursor.TakeValue(); break;
                default:
                    // A negative number is an operand, not an option.
                    if (option.Length > 1 && (char.IsAsciiDigit(option[1]) || option[1] == '.'))
                    {
                        cursor.Operands.Add(option);
                        break;
                    }

                    return ValueTask.FromResult(ExecResult.Usage("seq", $"invalid option -- '{option.TrimStart('-')}'"));
            }
        }

        var operands = cursor.Operands;
        if (operands.Count is 0 or > 3)
        {
            return ValueTask.FromResult(ExecResult.Usage("seq", "missing operand", ExitCodes.Failure));
        }

        decimal first = 1, increment = 1, last;

        if (!TryParse(operands[^1], out last))
        {
            return ValueTask.FromResult(ExecResult.Usage("seq", $"invalid floating point argument: {operands[^1]}"));
        }

        if (operands.Count >= 2 && !TryParse(operands[0], out first))
        {
            return ValueTask.FromResult(ExecResult.Usage("seq", $"invalid floating point argument: {operands[0]}"));
        }

        if (operands.Count == 3 && !TryParse(operands[1], out increment))
        {
            return ValueTask.FromResult(ExecResult.Usage("seq", $"invalid floating point argument: {operands[1]}"));
        }

        if (increment == 0)
        {
            return ValueTask.FromResult(ExecResult.Usage("seq", "zero increment", ExitCodes.Failure));
        }

        var decimals = Math.Max(Decimals(operands.Count >= 2 ? operands[0] : "1"),
            Math.Max(Decimals(operands.Count == 3 ? operands[1] : "1"), Decimals(operands[^1])));

        var values = new List<string>();
        var width = 0;

        for (var value = first; increment > 0 ? value <= last : value >= last; value += increment)
        {
            var text = value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            width = Math.Max(width, text.Length);
            values.Add(text);

            if (values.Count > context.Budget.Limits.MaxLoopIterations)
            {
                throw new LimitExceededException("max_loop_iterations", context.Budget.Limits.MaxLoopIterations);
            }
        }

        if (equalWidth)
        {
            for (var i = 0; i < values.Count; i++)
            {
                values[i] = values[i].PadLeft(width, '0');
            }
        }

        var output = values.Count == 0 ? string.Empty : string.Join(separator, values) + "\n";
        return ValueTask.FromResult(ExecResult.Ok(output));
    }

    private static bool TryParse(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static int Decimals(string text)
    {
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? 0 : text.Length - dot - 1;
    }
}

/// <summary><c>yes</c> — repeats a string. Bounded by the execution limits, unlike the real thing.</summary>
public sealed class YesBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "yes";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var text = context.Arguments.Count == 0 ? "y" : string.Join(' ', context.Arguments);
        var line = text + "\n";

        // An unbounded `yes` would hang the sandbox, so it fills the output budget and
        // stops — which is what a real `yes` piped into a bounded consumer looks like.
        var limit = context.Budget.Limits.MaxOutputBytes;
        var builder = new StringBuilder();

        while (builder.Length + line.Length <= limit)
        {
            builder.Append(line);
        }

        return ValueTask.FromResult(ExecResult.Ok(builder.ToString()));
    }
}
