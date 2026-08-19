using System.Globalization;
using System.Text;

namespace Bashkit.Builtins;

/// <summary><c>sort</c> — sorts lines.</summary>
public sealed class SortBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "sort";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var numeric = false;
        var reverse = false;
        var unique = false;
        var ignoreCase = false;
        var ignoreLeadingBlanks = false;
        var checkOnly = false;
        string? fieldSeparator = null;
        var keys = new List<string>();

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-n" or "--numeric-sort" or "-g" or "--general-numeric-sort": numeric = true; break;
                case "-r" or "--reverse": reverse = true; break;
                case "-u" or "--unique": unique = true; break;
                case "-f" or "--ignore-case": ignoreCase = true; break;
                case "-b" or "--ignore-leading-blanks": ignoreLeadingBlanks = true; break;
                case "-c" or "--check": checkOnly = true; break;
                case "-t" or "--field-separator": fieldSeparator = cursor.TakeValue(); break;
                case "-k" or "--key":
                    if (cursor.TakeValue() is { } key)
                    {
                        keys.Add(key);
                    }

                    break;

                case "-s" or "--stable" or "-V" or "--version-sort": break;
                case "-o" or "--output": cursor.TakeValue(); break;
                default:
                    return ExecResult.Usage("sort", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        var errors = new StringBuilder();
        var inputs = await TextHelpers.ReadInputsAsync(context, cursor.Operands, "sort", errors, cancellationToken);
        var lines = new List<string>();

        foreach (var (_, content) in inputs)
        {
            lines.AddRange(TextHelpers.SplitLines(content));
        }

        var comparer = new LineComparer(numeric, ignoreCase, ignoreLeadingBlanks, fieldSeparator, keys);

        if (checkOnly)
        {
            for (var i = 1; i < lines.Count; i++)
            {
                if (comparer.Compare(lines[i - 1], lines[i]) > 0)
                {
                    return ExecResult.Error(
                        $"sort: -:{i + 1}: disorder: {lines[i]}\n", ExitCodes.Failure);
                }
            }

            return ExecResult.Success;
        }

        // A stable sort keeps equal lines in input order, which `sort -s` requires and
        // which never hurts otherwise.
        var sorted = lines.OrderBy(static line => line, comparer).ToList();

        if (reverse)
        {
            sorted.Reverse();
        }

        if (unique)
        {
            var deduplicated = new List<string>(sorted.Count);
            foreach (var line in sorted)
            {
                if (deduplicated.Count == 0 || comparer.Compare(deduplicated[^1], line) != 0)
                {
                    deduplicated.Add(line);
                }
            }

            sorted = deduplicated;
        }

        var output = TextHelpers.JoinLines(sorted);

        return errors.Length == 0
            ? ExecResult.Ok(output)
            : new ExecResult
            {
                Stdout = StreamData.FromText(output),
                Stderr = StreamData.FromText(errors.ToString()),
                ExitCode = ExitCodes.Failure,
            };
    }

    /// <summary>Orders lines according to the selected sort options and key fields.</summary>
    private sealed class LineComparer(
        bool numeric,
        bool ignoreCase,
        bool ignoreLeadingBlanks,
        string? fieldSeparator,
        List<string> keys) : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            var left = x ?? string.Empty;
            var right = y ?? string.Empty;

            if (keys.Count > 0)
            {
                foreach (var key in keys)
                {
                    var result = CompareByKey(left, right, key);
                    if (result != 0)
                    {
                        return result;
                    }
                }

                return 0;
            }

            return CompareValues(left, right, numeric);
        }

        private int CompareByKey(string left, string right, string key)
        {
            // A key is `start[.char][opts][,end[.char][opts]]`; only the field index and
            // the `n` modifier are honoured here.
            var keyNumeric = numeric || key.Contains('n', StringComparison.Ordinal);
            var spec = key.Split(',')[0];
            var digits = new string(spec.TakeWhile(char.IsAsciiDigit).ToArray());

            if (!int.TryParse(digits, CultureInfo.InvariantCulture, out var index) || index < 1)
            {
                return CompareValues(left, right, keyNumeric);
            }

            return CompareValues(Field(left, index), Field(right, index), keyNumeric);
        }

        private string Field(string line, int index)
        {
            var fields = fieldSeparator is null
                ? line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                : line.Split(fieldSeparator);

            return index <= fields.Length ? fields[index - 1] : string.Empty;
        }

        private int CompareValues(string left, string right, bool asNumbers)
        {
            if (ignoreLeadingBlanks)
            {
                left = left.TrimStart();
                right = right.TrimStart();
            }

            if (asNumbers)
            {
                var leftValue = ParseLeadingNumber(left);
                var rightValue = ParseLeadingNumber(right);
                var result = leftValue.CompareTo(rightValue);
                return result != 0 ? result : string.CompareOrdinal(left, right);
            }

            return ignoreCase
                ? string.Compare(left, right, StringComparison.OrdinalIgnoreCase)
                : string.CompareOrdinal(left, right);
        }

        /// <summary>Reads a leading number, treating a non-numeric line as 0 as sort does.</summary>
        private static double ParseLeadingNumber(string text)
        {
            var span = text.AsSpan().TrimStart();
            var end = 0;

            if (end < span.Length && (span[end] == '-' || span[end] == '+'))
            {
                end++;
            }

            while (end < span.Length && (char.IsAsciiDigit(span[end]) || span[end] == '.'))
            {
                end++;
            }

            return double.TryParse(span[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
        }
    }
}

/// <summary><c>uniq</c> — collapses adjacent duplicate lines.</summary>
public sealed class UniqBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "uniq";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var count = false;
        var duplicatesOnly = false;
        var uniqueOnly = false;
        var ignoreCase = false;
        var skipFields = 0;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-c" or "--count": count = true; break;
                case "-d" or "--repeated": duplicatesOnly = true; break;
                case "-u" or "--unique": uniqueOnly = true; break;
                case "-i" or "--ignore-case": ignoreCase = true; break;
                case "-f" or "--skip-fields":
                    int.TryParse(cursor.TakeValue(), CultureInfo.InvariantCulture, out skipFields);
                    break;

                default:
                    return ExecResult.Usage("uniq", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        var errors = new StringBuilder();
        var inputs = await TextHelpers.ReadInputsAsync(context, cursor.Operands.Take(1).ToList(), "uniq", errors, cancellationToken);
        var lines = inputs.Count > 0 ? TextHelpers.SplitLines(inputs[0].Content) : [];

        var builder = new StringBuilder();
        var index = 0;

        while (index < lines.Length)
        {
            var run = 1;
            while (index + run < lines.Length && KeysEqual(lines[index], lines[index + run], skipFields, ignoreCase))
            {
                run++;
            }

            var emit = (duplicatesOnly, uniqueOnly) switch
            {
                (true, false) => run > 1,
                (false, true) => run == 1,
                _ => true,
            };

            if (emit)
            {
                if (count)
                {
                    builder.Append(run.ToString(CultureInfo.InvariantCulture).PadLeft(7)).Append(' ');
                }

                builder.Append(lines[index]).Append('\n');
            }

            index += run;
        }

        return ExecResult.Ok(builder.ToString());
    }

    private static bool KeysEqual(string left, string right, int skipFields, bool ignoreCase)
    {
        var a = SkipFields(left, skipFields);
        var b = SkipFields(right, skipFields);
        return string.Equals(a, b, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string SkipFields(string line, int count)
    {
        if (count <= 0)
        {
            return line;
        }

        var index = 0;
        for (var i = 0; i < count && index < line.Length; i++)
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
            {
                index++;
            }

            while (index < line.Length && !char.IsWhiteSpace(line[index]))
            {
                index++;
            }
        }

        return line[Math.Min(index, line.Length)..];
    }
}

/// <summary><c>rev</c> — reverses the characters of each line.</summary>
public sealed class RevBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "rev";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var errors = new StringBuilder();
        var inputs = await TextHelpers.ReadInputsAsync(context, context.Arguments, "rev", errors, cancellationToken);
        var builder = new StringBuilder();

        foreach (var (_, content) in inputs)
        {
            foreach (var line in TextHelpers.SplitLines(content))
            {
                var characters = line.ToCharArray();
                Array.Reverse(characters);
                builder.Append(characters).Append('\n');
            }
        }

        return ExecResult.Ok(builder.ToString());
    }
}

/// <summary><c>tac</c> — prints lines in reverse order.</summary>
public sealed class TacBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "tac";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var errors = new StringBuilder();
        var inputs = await TextHelpers.ReadInputsAsync(context, context.Arguments, "tac", errors, cancellationToken);
        var lines = new List<string>();

        foreach (var (_, content) in inputs)
        {
            lines.AddRange(TextHelpers.SplitLines(content));
        }

        lines.Reverse();
        return ExecResult.Ok(TextHelpers.JoinLines(lines));
    }
}
