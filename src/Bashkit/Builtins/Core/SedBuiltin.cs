using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bashkit.Builtins;

/// <summary>
/// <c>sed</c> — a stream editor.
/// </summary>
/// <remarks>
/// <para>
/// The implementation is a small interpreter over parsed commands rather than a set of
/// special cases, because sed scripts compose: <c>-e</c> may be repeated, commands may
/// carry one or two addresses, and blocks nest. Doing anything less falls over on the
/// second real script it meets.
/// </para>
/// <para>
/// Patterns are POSIX BREs by default (<c>-E</c> switches to EREs), translated by
/// <see cref="BasicRegex"/>, and every match runs under a timeout.
/// </para>
/// </remarks>
public sealed class SedBuiltin : IBuiltin
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public string Name => "sed";

    /// <inheritdoc />
    public string? LlmHint =>
        "sed: Stream editor. Supports s///[gip], d, p, a, i, c, y, q, =, ranges, -n, -e, -E, -i.";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var scripts = new List<string>();
        var quiet = false;
        var extended = false;
        var inPlace = false;
        var separate = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-n" or "--quiet" or "--silent": quiet = true; break;
                case "-E" or "-r" or "--regexp-extended": extended = true; break;
                case "-s" or "--separate": separate = true; break;
                case "-i" or "--in-place":
                    inPlace = true;
                    // GNU sed takes an optional suffix attached to -i; a detached value
                    // would be the script, so only an attached one is consumed.
                    if (cursor.PendingValue is not null)
                    {
                        cursor.TakeValue();
                    }

                    break;

                case "-e" or "--expression":
                    if (cursor.TakeValue() is { } script)
                    {
                        scripts.Add(script);
                    }

                    break;

                case "-f" or "--file":
                    if (cursor.TakeValue() is { } file)
                    {
                        var bytes = await context.FileSystem.ReadFileAsync(context.ResolvePath(file), cancellationToken);
                        scripts.Add(Encoding.UTF8.GetString(bytes));
                    }

                    break;

                case "-z" or "--null-data" or "--posix" or "-u": break;
                default:
                    return ExecResult.Usage("sed", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        var operands = cursor.Operands;

        if (scripts.Count == 0)
        {
            if (operands.Count == 0)
            {
                return ExecResult.Usage("sed", "no script specified");
            }

            scripts.Add(operands[0]);
            operands = operands[1..];
        }

        List<SedCommand> program;
        try
        {
            program = SedParser.Parse(string.Join('\n', scripts), extended);
        }
        catch (FormatException e)
        {
            return ExecResult.Usage("sed", e.Message);
        }

        var errors = new StringBuilder();
        var inputs = await TextHelpers.ReadInputsAsync(context, operands, "sed", errors, cancellationToken);
        var builder = new StringBuilder();
        var exitCode = errors.Length == 0 ? 0 : ExitCodes.Failure;

        if (separate || inPlace)
        {
            for (var i = 0; i < inputs.Count; i++)
            {
                var output = new StringBuilder();
                Run(program, TextHelpers.SplitLines(inputs[i].Content), quiet, output);

                if (inPlace && inputs[i].Name != "-")
                {
                    await context.FileSystem.WriteFileAsync(
                        context.ResolvePath(inputs[i].Name),
                        Encoding.UTF8.GetBytes(output.ToString()),
                        cancellationToken);
                }
                else
                {
                    builder.Append(output);
                }
            }
        }
        else
        {
            var lines = inputs.SelectMany(static i => TextHelpers.SplitLines(i.Content)).ToArray();
            Run(program, lines, quiet, builder);
        }

        return new ExecResult
        {
            Stdout = StreamData.FromText(builder.ToString()),
            Stderr = StreamData.FromText(errors.ToString()),
            ExitCode = exitCode,
        };
    }

    /// <summary>Runs the parsed program over the input lines.</summary>
    private static void Run(List<SedCommand> program, string[] lines, bool quiet, StringBuilder output)
    {
        var state = new SedState(lines.Length);

        for (var index = 0; index < lines.Length; index++)
        {
            state.LineNumber = index + 1;
            state.PatternSpace = lines[index];
            state.Deleted = false;
            state.Appended.Clear();

            Execute(program, state, quiet, output);

            if (!state.Deleted && !quiet)
            {
                output.Append(state.PatternSpace).Append('\n');
            }

            foreach (var appended in state.Appended)
            {
                output.Append(appended).Append('\n');
            }

            if (state.Quit)
            {
                return;
            }
        }
    }

    private static void Execute(List<SedCommand> program, SedState state, bool quiet, StringBuilder output)
    {
        foreach (var command in program)
        {
            if (state.Deleted || state.Quit)
            {
                return;
            }

            if (!command.Address.Matches(state))
            {
                continue;
            }

            switch (command.Kind)
            {
                case SedKind.Substitute:
                    state.PatternSpace = command.Substitute!.Apply(state.PatternSpace, out var changed);
                    if (changed && command.Substitute.Print)
                    {
                        output.Append(state.PatternSpace).Append('\n');
                    }

                    break;

                case SedKind.Delete:
                    state.Deleted = true;
                    return;

                case SedKind.Print:
                    output.Append(state.PatternSpace).Append('\n');
                    break;

                case SedKind.PrintLineNumber:
                    output.Append(state.LineNumber.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    break;

                case SedKind.Append:
                    state.Appended.Add(command.Text ?? string.Empty);
                    break;

                case SedKind.Insert:
                    output.Append(command.Text).Append('\n');
                    break;

                case SedKind.Change:
                    state.Deleted = true;
                    output.Append(command.Text).Append('\n');
                    return;

                case SedKind.Transliterate:
                    state.PatternSpace = Transliterate(state.PatternSpace, command.From!, command.To!);
                    break;

                case SedKind.Quit:
                    if (!quiet)
                    {
                        output.Append(state.PatternSpace).Append('\n');
                    }

                    state.Deleted = true;
                    state.Quit = true;
                    return;

                case SedKind.Block:
                    Execute(command.Block!, state, quiet, output);
                    break;
            }
        }
    }

    private static string Transliterate(string text, string from, string to)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            var index = from.IndexOf(c, StringComparison.Ordinal);
            builder.Append(index >= 0 && index < to.Length ? to[index] : c);
        }

        return builder.ToString();
    }

    /// <summary>The mutable state one input line is processed against.</summary>
    private sealed class SedState(int totalLines)
    {
        public string PatternSpace { get; set; } = string.Empty;

        public int LineNumber { get; set; }

        public int TotalLines { get; } = totalLines;

        public bool Deleted { get; set; }

        public bool Quit { get; set; }

        public List<string> Appended { get; } = [];
    }

    private enum SedKind
    {
        Substitute,
        Delete,
        Print,
        PrintLineNumber,
        Append,
        Insert,
        Change,
        Transliterate,
        Quit,
        Block,
    }

    private sealed record SedCommand(SedKind Kind, SedAddress Address)
    {
        public Substitution? Substitute { get; init; }

        public string? Text { get; init; }

        public string? From { get; init; }

        public string? To { get; init; }

        public List<SedCommand>? Block { get; init; }
    }

    /// <summary>
    /// A command's address: nothing (every line), one line or pattern, or a range.
    /// A range is stateful — once entered it stays active until its end matches.
    /// </summary>
    private sealed class SedAddress
    {
        public static SedAddress Always { get; } = new();

        public int? Line { get; init; }

        public Regex? Pattern { get; init; }

        public bool Last { get; init; }

        public int? EndLine { get; init; }

        public Regex? EndPattern { get; init; }

        public bool EndLast { get; init; }

        public bool IsRange { get; init; }

        public bool Negated { get; init; }

        private bool _inRange;

        public bool Matches(SedState state)
        {
            var result = Evaluate(state);
            return Negated ? !result : result;
        }

        private bool Evaluate(SedState state)
        {
            if (!IsRange)
            {
                return MatchesStart(state);
            }

            if (_inRange)
            {
                if (MatchesEnd(state))
                {
                    _inRange = false;
                }

                return true;
            }

            if (!MatchesStart(state))
            {
                return false;
            }

            // A range whose end is a line number already passed closes immediately.
            _inRange = !(EndLine is { } end && end <= state.LineNumber);
            return true;
        }

        private bool MatchesStart(SedState state)
        {
            if (Last)
            {
                return state.LineNumber == state.TotalLines;
            }

            if (Line is { } line)
            {
                return state.LineNumber == line;
            }

            if (Pattern is not null)
            {
                return SafeMatch(Pattern, state.PatternSpace);
            }

            return true;
        }

        private bool MatchesEnd(SedState state)
        {
            if (EndLast)
            {
                return state.LineNumber >= state.TotalLines;
            }

            if (EndLine is { } line)
            {
                return state.LineNumber >= line;
            }

            return EndPattern is not null && SafeMatch(EndPattern, state.PatternSpace);
        }
    }

    private static bool SafeMatch(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>An <c>s///</c> substitution with its flags.</summary>
    private sealed class Substitution(Regex pattern, string replacement, bool global, int occurrence, bool print)
    {
        public bool Print { get; } = print;

        public string Apply(string input, out bool changed)
        {
            var count = 0;
            var didChange = false;

            string Evaluate(Match match)
            {
                count++;

                if (!global && count != occurrence)
                {
                    return match.Value;
                }

                if (global && count < occurrence)
                {
                    return match.Value;
                }

                didChange = true;
                return ExpandReplacement(replacement, match);
            }

            try
            {
                var result = pattern.Replace(input, Evaluate);
                changed = didChange;
                return result;
            }
            catch (RegexMatchTimeoutException)
            {
                changed = false;
                return input;
            }
        }

        /// <summary>
        /// Expands the replacement text: <c>&amp;</c> is the whole match, <c>\1</c>..<c>\9</c>
        /// are groups, and <c>\n</c>/<c>\t</c> are escapes.
        /// </summary>
        private static string ExpandReplacement(string replacement, Match match)
        {
            var builder = new StringBuilder(replacement.Length);

            for (var i = 0; i < replacement.Length; i++)
            {
                var c = replacement[i];

                if (c == '&')
                {
                    builder.Append(match.Value);
                    continue;
                }

                if (c != '\\' || i + 1 >= replacement.Length)
                {
                    builder.Append(c);
                    continue;
                }

                var next = replacement[++i];

                switch (next)
                {
                    case >= '0' and <= '9':
                    {
                        var group = next - '0';
                        builder.Append(group < match.Groups.Count ? match.Groups[group].Value : string.Empty);
                        break;
                    }

                    case 'n': builder.Append('\n'); break;
                    case 't': builder.Append('\t'); break;
                    case 'r': builder.Append('\r'); break;
                    case '\\': builder.Append('\\'); break;
                    case '&': builder.Append('&'); break;
                    default: builder.Append(next); break;
                }
            }

            return builder.ToString();
        }
    }

    /// <summary>Parses a sed script into commands.</summary>
    private static class SedParser
    {
        public static List<SedCommand> Parse(string script, bool extended)
        {
            var position = 0;
            return ParseBlock(script, ref position, extended, terminator: '\0');
        }

        private static List<SedCommand> ParseBlock(string script, ref int position, bool extended, char terminator)
        {
            var commands = new List<SedCommand>();

            while (position < script.Length)
            {
                SkipSeparators(script, ref position);

                if (position >= script.Length)
                {
                    break;
                }

                if (script[position] == terminator)
                {
                    position++;
                    return commands;
                }

                if (script[position] == '#')
                {
                    while (position < script.Length && script[position] != '\n')
                    {
                        position++;
                    }

                    continue;
                }

                commands.Add(ParseCommand(script, ref position, extended));
            }

            return commands;
        }

        private static void SkipSeparators(string script, ref int position)
        {
            while (position < script.Length && (char.IsWhiteSpace(script[position]) || script[position] == ';'))
            {
                position++;
            }
        }

        private static SedCommand ParseCommand(string script, ref int position, bool extended)
        {
            var address = ParseAddress(script, ref position, extended);

            while (position < script.Length && script[position] == ' ')
            {
                position++;
            }

            if (position >= script.Length)
            {
                throw new FormatException("missing command");
            }

            var kind = script[position++];

            switch (kind)
            {
                case 's':
                    return new SedCommand(SedKind.Substitute, address)
                    {
                        Substitute = ParseSubstitution(script, ref position, extended),
                    };

                case 'y':
                {
                    var (from, to) = ParseTransliteration(script, ref position);
                    return new SedCommand(SedKind.Transliterate, address) { From = from, To = to };
                }

                case 'd':
                    return new SedCommand(SedKind.Delete, address);

                case 'p':
                    return new SedCommand(SedKind.Print, address);

                case '=':
                    return new SedCommand(SedKind.PrintLineNumber, address);

                case 'q':
                    return new SedCommand(SedKind.Quit, address);

                case 'a' or 'i' or 'c':
                {
                    var text = ParseText(script, ref position);
                    var mapped = kind switch
                    {
                        'a' => SedKind.Append,
                        'i' => SedKind.Insert,
                        _ => SedKind.Change,
                    };

                    return new SedCommand(mapped, address) { Text = text };
                }

                case '{':
                    return new SedCommand(SedKind.Block, address)
                    {
                        Block = ParseBlock(script, ref position, extended, terminator: '}'),
                    };

                default:
                    throw new FormatException($"unknown command: `{kind}'");
            }
        }

        private static SedAddress ParseAddress(string script, ref int position, bool extended)
        {
            while (position < script.Length && script[position] == ' ')
            {
                position++;
            }

            if (position >= script.Length)
            {
                return SedAddress.Always;
            }

            var (line, pattern, last, found) = ParseOneAddress(script, ref position, extended);

            if (!found)
            {
                return SedAddress.Always;
            }

            if (position < script.Length && script[position] == ',')
            {
                position++;
                var (endLine, endPattern, endLast, _) = ParseOneAddress(script, ref position, extended);
                var range = new SedAddress
                {
                    Line = line,
                    Pattern = pattern,
                    Last = last,
                    EndLine = endLine,
                    EndPattern = endPattern,
                    EndLast = endLast,
                    IsRange = true,
                    Negated = ConsumeNegation(script, ref position),
                };

                return range;
            }

            return new SedAddress
            {
                Line = line,
                Pattern = pattern,
                Last = last,
                Negated = ConsumeNegation(script, ref position),
            };
        }

        private static bool ConsumeNegation(string script, ref int position)
        {
            while (position < script.Length && script[position] == ' ')
            {
                position++;
            }

            if (position < script.Length && script[position] == '!')
            {
                position++;
                return true;
            }

            return false;
        }

        private static (int? Line, Regex? Pattern, bool Last, bool Found) ParseOneAddress(string script, ref int position, bool extended)
        {
            while (position < script.Length && script[position] == ' ')
            {
                position++;
            }

            if (position >= script.Length)
            {
                return (null, null, false, false);
            }

            if (script[position] == '$')
            {
                position++;
                return (null, null, true, true);
            }

            if (char.IsAsciiDigit(script[position]))
            {
                var start = position;
                while (position < script.Length && char.IsAsciiDigit(script[position]))
                {
                    position++;
                }

                return (int.Parse(script[start..position], CultureInfo.InvariantCulture), null, false, true);
            }

            if (script[position] == '/')
            {
                position++;
                var text = ReadDelimited(script, ref position, '/');
                return (null, CompileRegex(text, extended, ReadRegexFlags(script, ref position)), false, true);
            }

            // `\cREGEXc` allows any delimiter.
            if (script[position] == '\\' && position + 1 < script.Length)
            {
                var delimiter = script[position + 1];
                position += 2;
                var text = ReadDelimited(script, ref position, delimiter);
                return (null, CompileRegex(text, extended, ReadRegexFlags(script, ref position)), false, true);
            }

            return (null, null, false, false);
        }

        private static bool ReadRegexFlags(string script, ref int position)
        {
            var ignoreCase = false;
            while (position < script.Length && script[position] is 'I' or 'M')
            {
                if (script[position] == 'I')
                {
                    ignoreCase = true;
                }

                position++;
            }

            return ignoreCase;
        }

        private static Substitution ParseSubstitution(string script, ref int position, bool extended)
        {
            if (position >= script.Length)
            {
                throw new FormatException("unterminated `s' command");
            }

            var delimiter = script[position++];
            var pattern = ReadDelimited(script, ref position, delimiter);
            var replacement = ReadDelimited(script, ref position, delimiter);

            var global = false;
            var print = false;
            var ignoreCase = false;
            var occurrence = 1;

            while (position < script.Length)
            {
                var c = script[position];

                if (c == 'g')
                {
                    global = true;
                    position++;
                    continue;
                }

                if (c == 'p')
                {
                    print = true;
                    position++;
                    continue;
                }

                if (c is 'i' or 'I')
                {
                    ignoreCase = true;
                    position++;
                    continue;
                }

                if (char.IsAsciiDigit(c))
                {
                    var start = position;
                    while (position < script.Length && char.IsAsciiDigit(script[position]))
                    {
                        position++;
                    }

                    occurrence = int.Parse(script[start..position], CultureInfo.InvariantCulture);
                    continue;
                }

                break;
            }

            return new Substitution(CompileRegex(pattern, extended, ignoreCase), replacement, global, occurrence, print);
        }

        private static (string From, string To) ParseTransliteration(string script, ref int position)
        {
            if (position >= script.Length)
            {
                throw new FormatException("unterminated `y' command");
            }

            var delimiter = script[position++];
            var from = ReadDelimited(script, ref position, delimiter);
            var to = ReadDelimited(script, ref position, delimiter);

            if (from.Length != to.Length)
            {
                throw new FormatException("strings for `y' command are different lengths");
            }

            return (from, to);
        }

        private static string ParseText(string script, ref int position)
        {
            // GNU allows `a text` as well as the POSIX `a\` followed by a line.
            if (position < script.Length && script[position] == '\\')
            {
                position++;
                if (position < script.Length && script[position] == '\n')
                {
                    position++;
                }
            }

            while (position < script.Length && script[position] == ' ')
            {
                position++;
            }

            var builder = new StringBuilder();

            while (position < script.Length && script[position] != '\n')
            {
                if (script[position] == '\\' && position + 1 < script.Length)
                {
                    position++;
                    builder.Append(script[position] == 'n' ? '\n' : script[position]);
                    position++;
                    continue;
                }

                builder.Append(script[position++]);
            }

            return builder.ToString();
        }

        private static string ReadDelimited(string script, ref int position, char delimiter)
        {
            var builder = new StringBuilder();

            while (position < script.Length && script[position] != delimiter)
            {
                if (script[position] == '\\' && position + 1 < script.Length)
                {
                    // A backslash-escaped delimiter is literal; every other escape is
                    // handed on untouched for the regex or replacement to interpret.
                    if (script[position + 1] == delimiter)
                    {
                        builder.Append(delimiter);
                        position += 2;
                        continue;
                    }

                    builder.Append(script[position]).Append(script[position + 1]);
                    position += 2;
                    continue;
                }

                builder.Append(script[position++]);
            }

            if (position < script.Length)
            {
                position++;
            }

            return builder.ToString();
        }

        private static Regex CompileRegex(string pattern, bool extended, bool ignoreCase)
        {
            var translated = extended ? pattern : BasicRegex.Translate(pattern);
            var options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;

            try
            {
                return new Regex(translated, options, MatchTimeout);
            }
            catch (ArgumentException)
            {
                throw new FormatException($"invalid regular expression: {pattern}");
            }
        }
    }
}
