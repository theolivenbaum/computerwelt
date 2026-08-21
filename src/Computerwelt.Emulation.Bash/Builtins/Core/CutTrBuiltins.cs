using System.Globalization;
using System.Text;

namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary><c>cut</c> — selects byte, character or field ranges from each line.</summary>
public sealed class CutBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "cut";

    /// <inheritdoc />
    public string? LlmHint => "cut: Extracts columns. -f fields with -d delimiter, -c characters, -b bytes.";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        Mode? mode = null;
        string? ranges = null;
        var delimiter = '\t';
        string? outputDelimiter = null;
        var onlyDelimited = false;
        var complement = false;
        var nullTerminated = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-f" or "--fields": mode = Mode.Fields; ranges = cursor.TakeValue(); break;
                case "-c" or "--characters": mode = Mode.Characters; ranges = cursor.TakeValue(); break;
                case "-b" or "--bytes": mode = Mode.Bytes; ranges = cursor.TakeValue(); break;
                case "-d" or "--delimiter":
                {
                    var value = cursor.TakeValue();
                    delimiter = value is { Length: > 0 } ? value[0] : '\0';
                    break;
                }

                case "--output-delimiter": outputDelimiter = cursor.TakeValue(); break;
                case "-s" or "--only-delimited": onlyDelimited = true; break;
                case "--complement": complement = true; break;
                case "-z" or "--zero-terminated": nullTerminated = true; break;
                case "-n": break;
                default:
                    return ExecResult.Usage("cut", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        if (mode is null || ranges is null)
        {
            return ExecResult.Usage(
                "cut",
                "you must specify a list of bytes, characters, or fields",
                ExitCodes.Failure);
        }

        List<Range> selection;
        try
        {
            selection = ParseRanges(ranges);
        }
        catch (FormatException e)
        {
            return ExecResult.Usage("cut", e.Message);
        }

        var errors = new StringBuilder();
        var inputs = await TextHelpers.ReadInputsAsync(context, cursor.Operands, "cut", errors, cancellationToken);
        var builder = new StringBuilder();

        foreach (var (_, content) in inputs)
        {
            var records = nullTerminated
                ? content.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                : TextHelpers.SplitLines(content);

            foreach (var line in records)
            {
                if (mode == Mode.Fields)
                {
                    // A line with no delimiter is passed through whole, unless -s.
                    if (!line.Contains(delimiter, StringComparison.Ordinal))
                    {
                        if (!onlyDelimited)
                        {
                            builder.Append(line).Append('\n');
                        }

                        continue;
                    }

                    var fields = line.Split(delimiter);
                    var selected = Select(fields.Length, selection, complement)
                        .Select(index => fields[index - 1]);

                    builder.Append(string.Join(outputDelimiter ?? delimiter.ToString(), selected)).Append('\n');
                    continue;
                }

                var characters = Select(line.Length, selection, complement)
                    .Select(index => line[index - 1]);

                builder.Append(string.Concat(characters)).Append('\n');
            }
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

    private enum Mode
    {
        Fields,
        Characters,
        Bytes,
    }

    /// <summary>A 1-based inclusive range; <see cref="To"/> of <see cref="int.MaxValue"/> means open-ended.</summary>
    private readonly record struct Range(int From, int To);

    /// <summary>
    /// Expands the ranges into ascending 1-based indices. Output is always in ascending
    /// order regardless of how the ranges were written — <c>cut -f3,1</c> prints field 1
    /// then field 3, which surprises people but is what cut does.
    /// </summary>
    private static IEnumerable<int> Select(int length, List<Range> ranges, bool complement)
    {
        var selected = new SortedSet<int>();

        foreach (var range in ranges)
        {
            var end = Math.Min(range.To, length);
            for (var i = Math.Max(1, range.From); i <= end; i++)
            {
                selected.Add(i);
            }
        }

        if (!complement)
        {
            return selected;
        }

        var inverted = new List<int>();
        for (var i = 1; i <= length; i++)
        {
            if (!selected.Contains(i))
            {
                inverted.Add(i);
            }
        }

        return inverted;
    }

    private static List<Range> ParseRanges(string text)
    {
        var ranges = new List<Range>();

        foreach (var piece in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = piece.IndexOf('-', StringComparison.Ordinal);

            if (dash < 0)
            {
                var single = ParseIndex(piece);
                ranges.Add(new Range(single, single));
                continue;
            }

            var fromText = piece[..dash];
            var toText = piece[(dash + 1)..];

            var from = fromText.Length == 0 ? 1 : ParseIndex(fromText);
            var to = toText.Length == 0 ? int.MaxValue : ParseIndex(toText);

            ranges.Add(new Range(from, to));
        }

        if (ranges.Count == 0)
        {
            throw new FormatException("invalid range");
        }

        return ranges;
    }

    private static int ParseIndex(string text) =>
        int.TryParse(text, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new FormatException($"invalid byte, character or field list: '{text}'");
}

/// <summary><c>tr</c> — translates, squeezes or deletes characters.</summary>
public sealed class TrBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "tr";

    /// <inheritdoc />
    public string? LlmHint => "tr: Translates characters. Supports ranges, classes like [:alpha:], -d, -s, -c.";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var delete = false;
        var squeeze = false;
        var complement = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-d" or "--delete": delete = true; break;
                case "-s" or "--squeeze-repeats": squeeze = true; break;
                case "-c" or "-C" or "--complement": complement = true; break;
                case "-t" or "--truncate-set1": break;
                default:
                    return ValueTask.FromResult(ExecResult.Usage("tr", $"invalid option -- '{option.TrimStart('-')}'"));
            }
        }

        if (cursor.Operands.Count == 0)
        {
            return ValueTask.FromResult(ExecResult.Usage("tr", "missing operand"));
        }

        var set1 = ExpandSet(cursor.Operands[0]);
        var set2 = cursor.Operands.Count > 1 ? ExpandSet(cursor.Operands[1]) : string.Empty;

        if (!delete && !squeeze && set2.Length == 0)
        {
            return ValueTask.FromResult(ExecResult.Usage("tr", "missing operand after '" + cursor.Operands[0] + "'"));
        }

        var input = context.StdinText;
        var builder = new StringBuilder(input.Length);
        var previousSqueezed = '\0';
        var hasPrevious = false;

        foreach (var c in input)
        {
            var inSet1 = complement ? !set1.Contains(c, StringComparison.Ordinal) : set1.Contains(c, StringComparison.Ordinal);

            if (delete && inSet1)
            {
                continue;
            }

            var output = c;

            if (!delete && inSet1 && set2.Length > 0)
            {
                // A short set2 repeats its final character, so `tr a-z x` maps everything
                // to `x`.
                var index = complement ? set2.Length - 1 : set1.IndexOf(c, StringComparison.Ordinal);
                output = set2[Math.Min(index < 0 ? 0 : index, set2.Length - 1)];
            }

            if (squeeze)
            {
                var squeezeSet = delete || set2.Length == 0 ? set1 : set2;
                var shouldSqueeze = complement && (delete || set2.Length == 0)
                    ? !squeezeSet.Contains(output, StringComparison.Ordinal)
                    : squeezeSet.Contains(output, StringComparison.Ordinal);

                if (shouldSqueeze && hasPrevious && output == previousSqueezed)
                {
                    continue;
                }
            }

            builder.Append(output);
            previousSqueezed = output;
            hasPrevious = true;
        }

        return ValueTask.FromResult(ExecResult.Ok(builder.ToString()));
    }

    /// <summary>
    /// Expands <c>tr</c> set notation: <c>a-z</c> ranges, <c>[:class:]</c> classes,
    /// <c>[c*n]</c> repeats and backslash escapes.
    /// </summary>
    internal static string ExpandSet(string spec)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < spec.Length; i++)
        {
            var c = spec[i];

            if (c == '\\' && i + 1 < spec.Length)
            {
                // `\NNN` is an octal character, which is how `\0` reaches tr as a NUL.
                if (IsOctal(spec[i + 1]))
                {
                    var value = 0;
                    var digits = 0;

                    while (digits < 3 && i + 1 < spec.Length && IsOctal(spec[i + 1]))
                    {
                        value = (value * 8) + (spec[++i] - '0');
                        digits++;
                    }

                    builder.Append((char)value);
                    continue;
                }

                builder.Append(Unescape(spec[++i]));
                continue;
            }

            if (c == '[' && i + 1 < spec.Length && spec[i + 1] == ':')
            {
                var close = spec.IndexOf(":]", i + 2, StringComparison.Ordinal);
                if (close > 0)
                {
                    AppendClass(builder, spec[(i + 2)..close]);
                    i = close + 1;
                    continue;
                }
            }

            // `[c*n]` repeats c n times; `[c*]` pads to fill, which is only meaningful for
            // set2 and is treated here as a single character.
            if (c == '[' && i + 2 < spec.Length && spec[i + 2] == '*')
            {
                var close = spec.IndexOf(']', i + 2);
                if (close > 0)
                {
                    var countText = spec[(i + 3)..close];
                    var count = int.TryParse(countText, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1;
                    builder.Append(new string(spec[i + 1], Math.Max(1, count)));
                    i = close;
                    continue;
                }
            }

            if (i + 2 < spec.Length && spec[i + 1] == '-' && spec[i + 2] != ']')
            {
                for (var value = c; value <= spec[i + 2]; value++)
                {
                    builder.Append(value);
                }

                i += 2;
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsOctal(char c) => c is >= '0' and <= '7';

    private static char Unescape(char c) => c switch
    {
        'n' => '\n',
        't' => '\t',
        'r' => '\r',
        'a' => '\a',
        'b' => '\b',
        'f' => '\f',
        'v' => '\v',
        '\\' => '\\',
        _ => c,
    };

    private static void AppendClass(StringBuilder builder, string className)
    {
        for (var c = (char)0; c < 128; c++)
        {
            var matches = className switch
            {
                "alpha" => char.IsAsciiLetter(c),
                "digit" => char.IsAsciiDigit(c),
                "alnum" => char.IsAsciiLetterOrDigit(c),
                "lower" => char.IsAsciiLetterLower(c),
                "upper" => char.IsAsciiLetterUpper(c),
                "space" => char.IsWhiteSpace(c),
                "blank" => c is ' ' or '\t',
                "punct" => !char.IsAsciiLetterOrDigit(c) && !char.IsControl(c) && c != ' ',
                "print" => !char.IsControl(c),
                "graph" => !char.IsControl(c) && c != ' ',
                "cntrl" => char.IsControl(c),
                "xdigit" => Uri.IsHexDigit(c),
                _ => false,
            };

            if (matches)
            {
                builder.Append(c);
            }
        }
    }
}

/// <summary><c>nl</c> — numbers lines.</summary>
public sealed class NlBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "nl";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var style = "t";
        var separator = "\t";
        var width = 6;
        var increment = 1;
        var start = 1;
        var format = "rn";

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-b" or "--body-numbering": style = cursor.TakeValue() ?? "t"; break;
                case "-s" or "--number-separator": separator = cursor.TakeValue() ?? "\t"; break;
                case "-w" or "--number-width":
                    int.TryParse(cursor.TakeValue(), CultureInfo.InvariantCulture, out width);
                    break;

                case "-i" or "--line-increment":
                    int.TryParse(cursor.TakeValue(), CultureInfo.InvariantCulture, out increment);
                    break;

                case "-v" or "--starting-line-number":
                    int.TryParse(cursor.TakeValue(), CultureInfo.InvariantCulture, out start);
                    break;

                case "-n" or "--number-format": format = cursor.TakeValue() ?? "rn"; break;
                case "-p" or "-l" or "-d" or "-f" or "-h": break;
                default:
                    return ExecResult.Usage("nl", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        var errors = new StringBuilder();
        var inputs = await TextHelpers.ReadInputsAsync(context, cursor.Operands, "nl", errors, cancellationToken);
        var builder = new StringBuilder();
        var number = start;

        foreach (var (_, content) in inputs)
        {
            foreach (var line in TextHelpers.SplitLines(content))
            {
                // Style `t` (the default) skips blank lines; `a` numbers everything;
                // `n` numbers nothing.
                var numbered = style switch
                {
                    "a" => true,
                    "n" => false,
                    _ => line.Length > 0,
                };

                if (numbered)
                {
                    builder.Append(Format(number, width, format))
                        .Append(separator).Append(line).Append('\n');
                    number += increment;
                }
                else
                {
                    // An unnumbered line is indented to where the text of a numbered one
                    // would start — the number field plus the separator.
                    builder.Append(new string(' ', width + separator.Length)).Append(line).Append('\n');
                }
            }
        }

        return ExecResult.Ok(builder.ToString());
    }

    /// <summary>Renders a line number in one of nl's three field formats.</summary>
    private static string Format(int number, int width, string format)
    {
        var text = number.ToString(CultureInfo.InvariantCulture);

        return format switch
        {
            "ln" => text.PadRight(width),
            "rz" => text.PadLeft(width, '0'),
            _ => text.PadLeft(width),
        };
    }
}

/// <summary><c>paste</c> — joins corresponding lines of its inputs.</summary>
public sealed class PasteBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "paste";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var delimiters = "\t";
        var serial = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-d" or "--delimiters":
                    if (cursor.TakeValue() is not { } value)
                    {
                        return ExecResult.Usage("paste", $"option requires an argument -- '{option.TrimStart('-')}'", ExitCodes.Failure);
                    }

                    delimiters = TrBuiltin.ExpandSet(value);
                    break;
                case "-s" or "--serial": serial = true; break;
                default:
                    return ExecResult.Usage("paste", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        if (delimiters.Length == 0)
        {
            delimiters = "\0";
        }

        var errors = new StringBuilder();
        var inputs = await TextHelpers.ReadInputsAsync(context, cursor.Operands, "paste", errors, cancellationToken);
        var columns = inputs.Select(static i => TextHelpers.SplitLines(i.Content)).ToList();
        var builder = new StringBuilder();

        if (serial)
        {
            // `-s` pastes each file onto one line instead of merging across files.
            foreach (var lines in columns)
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(delimiters[(i - 1) % delimiters.Length]);
                    }

                    builder.Append(lines[i]);
                }

                builder.Append('\n');
            }

            return ExecResult.Ok(builder.ToString());
        }

        var height = columns.Count == 0 ? 0 : columns.Max(static c => c.Length);

        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < columns.Count; column++)
            {
                if (column > 0)
                {
                    builder.Append(delimiters[(column - 1) % delimiters.Length]);
                }

                if (row < columns[column].Length)
                {
                    builder.Append(columns[column][row]);
                }
            }

            builder.Append('\n');
        }

        return ExecResult.Ok(builder.ToString());
    }
}

/// <summary><c>tee</c> — copies standard input to files and to standard output.</summary>
public sealed class TeeBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "tee";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var append = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-a" or "--append": append = true; break;
                case "-i" or "--ignore-interrupts" or "-p": break;
                default:
                    return ExecResult.Usage("tee", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        var payload = context.Stdin ?? StreamData.Empty;
        var errors = new StringBuilder();

        foreach (var operand in cursor.Operands)
        {
            try
            {
                var path = context.ResolvePath(operand);
                if (append)
                {
                    await context.FileSystem.AppendFileAsync(path, payload.Memory, cancellationToken);
                }
                else
                {
                    await context.FileSystem.WriteFileAsync(path, payload.Memory, cancellationToken);
                }
            }
            catch (FileSystemException e)
            {
                errors.Append("tee: ").Append(operand).Append(": ").Append(MkdirBuiltin.Describe(e)).Append('\n');
            }
        }

        return new ExecResult
        {
            Stdout = payload,
            Stderr = StreamData.FromText(errors.ToString()),
            ExitCode = errors.Length == 0 ? 0 : ExitCodes.Failure,
        };
    }
}
