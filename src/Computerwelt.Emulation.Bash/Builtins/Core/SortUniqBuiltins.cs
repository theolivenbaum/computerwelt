using System.Globalization;
using System.Text;

namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary><c>sort</c> — sorts lines.</summary>
/// <remarks>
/// <para>
/// The ordering is a list of keys compared in turn, each with its own dialect — lexical,
/// numeric, human-readable sizes, month names or version numbers — followed, unless
/// <c>-s</c> forbids it, by a last-resort comparison of the whole line. Modelling it that
/// way is what lets <c>-k2n,2 -k1,1r</c> mean what POSIX says it means, instead of
/// approximating each flag combination separately.
/// </para>
/// </remarks>
public sealed class SortBuiltin : IBuiltin
{
    private static readonly string[] Months =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    /// <inheritdoc />
    public string Name => "sort";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: sort [OPTION]... [FILE]...
        Write the sorted concatenation of all FILE(s) to standard output.

          -n    compare numerically
          -h    compare human-readable sizes (2K, 1G)
          -M    compare month names
          -V    compare version numbers naturally
          -r    reverse the result
          -u    output only the first of an equal run
          -s    stable: do not compare whole lines as a last resort
          -f    fold case
          -k    sort by a key
          -t    use a field separator
          -z    lines are terminated by NUL rather than newline
          -o    write to a file instead of standard output
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (CommandHelp.Handle(context.Arguments, Help!, "sort 0.1.0") is { } help)
        {
            return help;
        }

        var cursor = new ArgCursor(context.Arguments);
        var global = new SortFlags();
        var unique = false;
        var stable = false;
        var checkOnly = false;
        var nullTerminated = false;
        string? fieldSeparator = null;
        string? outputFile = null;
        var keys = new List<SortKey>();

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-n" or "--numeric-sort" or "-g" or "--general-numeric-sort": global.Numeric = true; break;
                case "-h" or "--human-numeric-sort": global.Human = true; break;
                case "-M" or "--month-sort": global.Month = true; break;
                case "-V" or "--version-sort": global.Version = true; break;
                case "-r" or "--reverse": global.Reverse = true; break;
                case "-f" or "--ignore-case": global.IgnoreCase = true; break;
                case "-b" or "--ignore-leading-blanks": global.IgnoreBlanks = true; break;
                case "-u" or "--unique": unique = true; break;
                case "-s" or "--stable": stable = true; break;
                case "-c" or "--check" or "-C" or "--check=silent": checkOnly = true; break;
                case "-z" or "--zero-terminated": nullTerminated = true; break;
                case "-t" or "--field-separator": fieldSeparator = cursor.TakeValue(); break;
                case "-o" or "--output": outputFile = cursor.TakeValue(); break;

                // `-m` merges already-sorted files, which a full sort of the concatenation
                // produces identically; the distinction is only about memory.
                case "-m" or "--merge" or "-d" or "--dictionary-order"
                    or "-i" or "--ignore-nonprinting" or "--debug" or "--stable=":
                    break;

                case "-S" or "--buffer-size" or "-T" or "--temporary-directory" or "--parallel":
                    cursor.TakeValue();
                    break;

                case "-k" or "--key":
                    if (cursor.TakeValue() is { } key)
                    {
                        keys.Add(SortKey.Parse(key));
                    }

                    break;

                default:
                    return ExecResult.Usage("sort", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        var errors = new StringBuilder();
        var inputs = await TextHelpers.ReadInputsAsync(context, cursor.Operands, "sort", errors, cancellationToken);
        var lines = new List<string>();

        foreach (var (_, content) in inputs)
        {
            lines.AddRange(nullTerminated
                ? content.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                : TextHelpers.SplitLines(content));
        }

        var comparer = new LineComparer(global, keys, fieldSeparator, stable);

        if (checkOnly)
        {
            for (var i = 1; i < lines.Count; i++)
            {
                if (comparer.Compare(lines[i - 1], lines[i]) > 0)
                {
                    return ExecResult.Error($"sort: -:{i + 1}: disorder: {lines[i]}\n", ExitCodes.Failure);
                }
            }

            return ExecResult.Success;
        }

        // OrderBy is a stable sort, which is what `-s` needs and what never hurts otherwise.
        var sorted = lines.OrderBy(static line => line, comparer).ToList();

        if (unique)
        {
            // `-u` deduplicates on the keys alone: the last-resort comparison would make
            // every distinct line unique and the flag meaningless.
            var deduplicated = new List<string>(sorted.Count);

            foreach (var line in sorted)
            {
                if (deduplicated.Count == 0 || comparer.CompareKeys(deduplicated[^1], line) != 0)
                {
                    deduplicated.Add(line);
                }
            }

            sorted = deduplicated;
        }

        var output = nullTerminated
            ? string.Concat(sorted.Select(static line => line + "\0"))
            : TextHelpers.JoinLines(sorted);

        if (outputFile is not null)
        {
            await context.FileSystem.WriteFileAsync(
                context.ResolvePath(outputFile),
                Encoding.UTF8.GetBytes(output),
                cancellationToken);

            output = string.Empty;
        }

        return errors.Length == 0
            ? ExecResult.Ok(output)
            : new ExecResult
            {
                Stdout = StreamData.FromText(output),
                Stderr = StreamData.FromText(errors.ToString()),
                ExitCode = ExitCodes.Failure,
            };
    }

    /// <summary>The comparison dialect a key or the command as a whole was asked for.</summary>
    private sealed class SortFlags
    {
        public bool Numeric;
        public bool Human;
        public bool Month;
        public bool Version;
        public bool IgnoreCase;
        public bool IgnoreBlanks;
        public bool Reverse;

        /// <summary>True when the flags pick an ordering rather than only modifying one.</summary>
        public bool HasOrdering => Numeric || Human || Month || Version;
    }

    /// <summary>A <c>-k</c> specification: <c>start[.char][flags][,end[.char][flags]]</c>.</summary>
    private sealed class SortKey
    {
        public int StartField { get; private init; } = 1;

        public int StartChar { get; private init; } = 1;

        public int? EndField { get; private init; }

        /// <summary>The last character of the end field, or zero for all of it.</summary>
        public int EndChar { get; private init; }

        public SortFlags Flags { get; private init; } = new();

        public static SortKey Parse(string specification)
        {
            var halves = specification.Split(',');
            var (startField, startChar, startFlags) = ParseHalf(halves[0]);

            if (halves.Length == 1)
            {
                return new SortKey
                {
                    StartField = Math.Max(1, startField),
                    StartChar = Math.Max(1, startChar),
                    Flags = startFlags,
                };
            }

            var (endField, endChar, endFlags) = ParseHalf(halves[1]);

            return new SortKey
            {
                StartField = Math.Max(1, startField),
                StartChar = Math.Max(1, startChar),
                EndField = endField > 0 ? endField : null,
                EndChar = Math.Max(0, endChar),

                // Flags may be written on either half; sort treats them as one set.
                Flags = Merge(startFlags, endFlags),
            };
        }

        private static (int Field, int Char, SortFlags Flags) ParseHalf(string half)
        {
            var index = 0;
            var field = ReadNumber(half, ref index);
            var character = 0;

            if (index < half.Length && half[index] == '.')
            {
                index++;
                character = ReadNumber(half, ref index);
            }

            var flags = new SortFlags();

            for (; index < half.Length; index++)
            {
                switch (half[index])
                {
                    case 'n' or 'g': flags.Numeric = true; break;
                    case 'h': flags.Human = true; break;
                    case 'M': flags.Month = true; break;
                    case 'V': flags.Version = true; break;
                    case 'f': flags.IgnoreCase = true; break;
                    case 'b': flags.IgnoreBlanks = true; break;
                    case 'r': flags.Reverse = true; break;
                    default: break;
                }
            }

            return (field, character, flags);
        }

        private static SortFlags Merge(SortFlags left, SortFlags right) => new()
        {
            Numeric = left.Numeric || right.Numeric,
            Human = left.Human || right.Human,
            Month = left.Month || right.Month,
            Version = left.Version || right.Version,
            IgnoreCase = left.IgnoreCase || right.IgnoreCase,
            IgnoreBlanks = left.IgnoreBlanks || right.IgnoreBlanks,
            Reverse = left.Reverse || right.Reverse,
        };

        private static int ReadNumber(string text, ref int index)
        {
            var start = index;

            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
            }

            return index == start ? 0 : int.Parse(text[start..index], CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Orders lines according to the selected sort options and key fields.</summary>
    private sealed class LineComparer(
        SortFlags global,
        List<SortKey> keys,
        string? fieldSeparator,
        bool stable) : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            var left = x ?? string.Empty;
            var right = y ?? string.Empty;
            var result = CompareKeys(left, right);

            if (result != 0 || stable)
            {
                return result;
            }

            // Unless `-s` forbids it, equal keys are broken by the whole line, which is what
            // makes an otherwise unstable sort deterministic.
            var lastResort = string.CompareOrdinal(left, right);
            return global.Reverse ? -lastResort : lastResort;
        }

        /// <summary>Compares by the keys alone, which is what <c>-u</c> deduplicates on.</summary>
        public int CompareKeys(string left, string right)
        {
            if (keys.Count == 0)
            {
                var whole = CompareText(left, right, global);
                return global.Reverse ? -whole : whole;
            }

            foreach (var key in keys)
            {
                // A key that names no ordering inherits the command's.
                var flags = key.Flags.HasOrdering ? key.Flags : Combine(key.Flags, global);
                var result = CompareText(Extract(left, key), Extract(right, key), flags);

                if (result != 0)
                {
                    return key.Flags.Reverse || global.Reverse ? -result : result;
                }
            }

            return 0;
        }

        private static SortFlags Combine(SortFlags key, SortFlags global) => new()
        {
            Numeric = global.Numeric,
            Human = global.Human,
            Month = global.Month,
            Version = global.Version,
            IgnoreCase = key.IgnoreCase || global.IgnoreCase,
            IgnoreBlanks = key.IgnoreBlanks || global.IgnoreBlanks,
        };

        /// <summary>Cuts the part of a line a key selects.</summary>
        private string Extract(string line, SortKey key)
        {
            var fields = fieldSeparator is null
                ? line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                : line.Split(fieldSeparator);

            if (key.StartField > fields.Length)
            {
                return string.Empty;
            }

            var last = Math.Min(key.EndField ?? fields.Length, fields.Length);

            if (last < key.StartField)
            {
                return string.Empty;
            }

            var selected = fields[(key.StartField - 1)..last];

            // The character offsets cut into the first and last fields only.
            if (key.StartChar > 1)
            {
                selected[0] = key.StartChar <= selected[0].Length ? selected[0][(key.StartChar - 1)..] : string.Empty;
            }

            if (key.EndChar > 0)
            {
                var tail = selected[^1];
                selected[^1] = key.EndChar < tail.Length ? tail[..key.EndChar] : tail;
            }

            return string.Join(fieldSeparator ?? " ", selected);
        }

        private static int CompareText(string left, string right, SortFlags flags)
        {
            if (flags.IgnoreBlanks)
            {
                left = left.TrimStart();
                right = right.TrimStart();
            }

            if (flags.Version)
            {
                return CompareVersions(left, right);
            }

            if (flags.Month)
            {
                return MonthOf(left).CompareTo(MonthOf(right));
            }

            if (flags.Human)
            {
                return ParseSize(left).CompareTo(ParseSize(right));
            }

            if (flags.Numeric)
            {
                return ParseLeadingNumber(left).CompareTo(ParseLeadingNumber(right));
            }

            return flags.IgnoreCase
                ? string.Compare(left, right, StringComparison.OrdinalIgnoreCase)
                : string.CompareOrdinal(left, right);
        }

        /// <summary>
        /// Compares naturally: runs of digits compare as numbers, everything else by code
        /// point, so <c>1.9</c> sorts before <c>1.10</c>.
        /// </summary>
        private static int CompareVersions(string left, string right)
        {
            int i = 0, j = 0;

            while (i < left.Length && j < right.Length)
            {
                if (char.IsAsciiDigit(left[i]) && char.IsAsciiDigit(right[j]))
                {
                    var startLeft = i;
                    while (i < left.Length && char.IsAsciiDigit(left[i]))
                    {
                        i++;
                    }

                    var startRight = j;
                    while (j < right.Length && char.IsAsciiDigit(right[j]))
                    {
                        j++;
                    }

                    var a = left[startLeft..i].TrimStart('0');
                    var b = right[startRight..j].TrimStart('0');

                    if (a.Length != b.Length)
                    {
                        return a.Length - b.Length;
                    }

                    var digits = string.CompareOrdinal(a, b);

                    if (digits != 0)
                    {
                        return digits;
                    }

                    continue;
                }

                if (left[i] != right[j])
                {
                    return left[i] - right[j];
                }

                i++;
                j++;
            }

            return (left.Length - i) - (right.Length - j);
        }

        private static int MonthOf(string text)
        {
            var trimmed = text.TrimStart();

            if (trimmed.Length < 3)
            {
                return 0;
            }

            // An unrecognised month sorts before every real one, as GNU sort does.
            return Array.IndexOf(Months, trimmed[..3].ToUpperInvariant()) + 1;
        }

        /// <summary>Reads a number with an optional binary size suffix, for <c>-h</c>.</summary>
        private static double ParseSize(string text)
        {
            var span = text.AsSpan().TrimStart();
            var end = Measure(span);

            if (!double.TryParse(span[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return 0;
            }

            var multiplier = end < span.Length
                ? span[end] switch
                {
                    'K' or 'k' => 1024.0,
                    'M' or 'm' => 1024.0 * 1024,
                    'G' or 'g' => 1024.0 * 1024 * 1024,
                    'T' or 't' => 1024.0 * 1024 * 1024 * 1024,
                    'P' or 'p' => 1024.0 * 1024 * 1024 * 1024 * 1024,
                    'E' or 'e' => 1024.0 * 1024 * 1024 * 1024 * 1024 * 1024,
                    _ => 1.0,
                }
                : 1.0;

            return value * multiplier;
        }

        /// <summary>Reads a leading number, treating a non-numeric line as 0 as sort does.</summary>
        private static double ParseLeadingNumber(string text)
        {
            var span = text.AsSpan().TrimStart();

            return double.TryParse(
                span[..Measure(span)],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
                ? value
                : 0;
        }

        /// <summary>The length of the numeric prefix of <paramref name="span"/>.</summary>
        private static int Measure(ReadOnlySpan<char> span)
        {
            var end = 0;

            if (end < span.Length && (span[end] == '-' || span[end] == '+'))
            {
                end++;
            }

            while (end < span.Length && (char.IsAsciiDigit(span[end]) || span[end] == '.'))
            {
                end++;
            }

            return end;
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
        var cursor = new ArgCursor(context.Arguments);

        while (cursor.NextOption() is { } option)
        {
            // `-s`, `-b` and `-r` change what a record is, which this reverses-by-line
            // implementation does not model; rejecting them beats silently ignoring them.
            return ExecResult.Usage("tac", $"invalid option -- '{option.TrimStart('-')}'", ExitCodes.Usage);
        }

        var errors = new StringBuilder();
        var inputs = await TextHelpers.ReadInputsAsync(context, cursor.Operands, "tac", errors, cancellationToken);
        var lines = new List<string>();

        foreach (var (_, content) in inputs)
        {
            lines.AddRange(TextHelpers.SplitLines(content));
        }

        lines.Reverse();
        return ExecResult.Ok(TextHelpers.JoinLines(lines));
    }
}
