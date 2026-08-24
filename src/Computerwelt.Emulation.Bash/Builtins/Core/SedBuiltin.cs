using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Computerwelt.Emulation.Bash.Builtins;

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

    /// <summary>How many instructions one input line may execute before the script is called looping.</summary>
    private const int MaxSteps = 1_000_000;

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

        SedProgram program;
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
        var files = await ResolveFilesAsync(context, program, cancellationToken);

        try
        {
            if (separate || inPlace)
            {
                for (var i = 0; i < inputs.Count; i++)
                {
                    var output = new StringBuilder();
                    var code = Run(program, TextHelpers.SplitLines(inputs[i].Content), quiet, output, files);

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

                    if (code != 0)
                    {
                        exitCode = code;
                    }
                }
            }
            else
            {
                var lines = inputs.SelectMany(static i => TextHelpers.SplitLines(i.Content)).ToArray();
                var code = Run(program, lines, quiet, builder, files);

                if (code != 0)
                {
                    exitCode = code;
                }
            }
        }
        catch (FormatException e)
        {
            return ExecResult.Usage("sed", e.Message, ExitCodes.Usage);
        }

        await FlushWritesAsync(context, files, cancellationToken);

        return new ExecResult
        {
            Stdout = StreamData.FromText(builder.ToString()),
            Stderr = StreamData.FromText(errors.ToString()),
            ExitCode = exitCode,
        };
    }

    /// <summary>
    /// Reads every file the script's <c>r</c> / <c>R</c> commands name, before the run.
    /// </summary>
    /// <remarks>
    /// A file that cannot be read is not an error: GNU treats it as empty, so a script that
    /// optionally splices in a header still works where the header does not exist.
    /// </remarks>
    private static async ValueTask<SedFiles> ResolveFilesAsync(BuiltinContext context, SedProgram program, CancellationToken cancellationToken)
    {
        var files = new SedFiles();

        foreach (var command in program.Commands)
        {
            if (command.Kind is not (SedKind.ReadFile or SedKind.ReadFileLine)) continue;

            var name = command.Text ?? string.Empty;

            if (name.Length == 0 || files.Reads.ContainsKey(name)) continue;

            try
            {
                var bytes = await context.FileSystem.ReadFileAsync(context.ResolvePath(name), cancellationToken);
                files.Reads[name] = TextHelpers.SplitLines(Encoding.UTF8.GetString(bytes));
            }
            catch (ShellException)
            {
                files.Reads[name] = [];
            }
        }

        return files;
    }

    /// <summary>Writes what <c>w</c> and <c>W</c> collected, once the run is over.</summary>
    private static async ValueTask FlushWritesAsync(BuiltinContext context, SedFiles files, CancellationToken cancellationToken)
    {
        foreach (var (name, content) in files.Writes)
        {
            //`/dev/stdout' is the one name GNU gives a meaning other than a path, and a script
            //that uses it to tee wants it on stdout, not in a file called /dev/stdout.
            if (name is "/dev/stdout" or "/dev/stderr") continue;

            await context.FileSystem.WriteFileAsync(
                context.ResolvePath(name),
                Encoding.UTF8.GetBytes(content.ToString()),
                cancellationToken);
        }
    }

    /// <summary>Runs the parsed program over the input lines.</summary>
    /// <returns>The exit code a <c>q</c> or <c>Q</c> asked for, or zero.</returns>
    private static int Run(SedProgram program, string[] lines, bool quiet, StringBuilder output, SedFiles files)
    {
        // Ranges are stateful, and `-s` runs the same program over each file in turn.
        program.Reset();

        var state = new SedState(lines);

        while (state.Index < lines.Length)
        {
            state.PatternSpace = lines[state.Index];
            state.Substituted = false;
            state.Appended.Clear();

            SedFlow flow;

            do
            {
                flow = Execute(program, state, quiet, output, files);
            }
            while (flow == SedFlow.Restart);

            if (flow is SedFlow.Normal or SedFlow.Quit && !quiet)
            {
                output.Append(state.PatternSpace).Append('\n');
            }

            // `Q` abandons the queued append text along with the pattern space.
            if (flow != SedFlow.QuitSilent)
            {
                Flush(state, output);
            }

            if (flow is SedFlow.Quit or SedFlow.QuitSilent)
            {
                break;
            }

            state.Index++;
        }

        return state.ExitCode;
    }

    /// <summary>Runs the program once over the current pattern space.</summary>
    /// <remarks>
    /// The program is a flat instruction list rather than a tree so that <c>b</c>, <c>t</c>
    /// and <c>T</c> can be a plain assignment to the program counter. A block is an
    /// instruction that either falls through into its body or jumps past it.
    /// </remarks>
    private static SedFlow Execute(SedProgram program, SedState state, bool quiet, StringBuilder output, SedFiles files)
    {
        var commands = program.Commands;
        var pc = 0;
        var steps = 0;

        while (pc < commands.Count)
        {
            // `:x; b x` is a legal script that never ends. Bounding the step count keeps a
            // runaway script an error rather than a hung sandbox.
            if (++steps > MaxSteps)
            {
                throw new FormatException("script does not terminate");
            }

            var command = commands[pc];

            if (!command.Address.Matches(state))
            {
                pc = command.Kind == SedKind.Block ? command.Jump : pc + 1;
                continue;
            }

            switch (command.Kind)
            {
                case SedKind.Substitute:
                    state.PatternSpace = command.Substitute!.Apply(state.PatternSpace, out var changed);
                    if (changed)
                    {
                        state.Substituted = true;

                        if (command.Substitute.Print)
                        {
                            output.Append(state.PatternSpace).Append('\n');
                        }
                    }

                    break;

                case SedKind.Delete:
                    return SedFlow.Deleted;

                case SedKind.DeleteFirstLine:
                {
                    var newline = state.PatternSpace.IndexOf('\n', StringComparison.Ordinal);

                    if (newline < 0)
                    {
                        return SedFlow.Deleted;
                    }

                    state.PatternSpace = state.PatternSpace[(newline + 1)..];
                    return SedFlow.Restart;
                }

                case SedKind.Print:
                    output.Append(state.PatternSpace).Append('\n');
                    break;

                case SedKind.PrintFirstLine:
                {
                    var newline = state.PatternSpace.IndexOf('\n', StringComparison.Ordinal);
                    output.Append(newline < 0 ? state.PatternSpace : state.PatternSpace[..newline]).Append('\n');
                    break;
                }

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
                    // Over a range, the replacement text stands for the whole range and so
                    // is emitted once, as the range closes.
                    if (command.Address.JustClosed)
                    {
                        output.Append(command.Text).Append('\n');
                    }

                    return SedFlow.Deleted;

                case SedKind.Transliterate:
                    state.PatternSpace = Transliterate(state.PatternSpace, command.From!, command.To!);
                    break;

                case SedKind.Quit:
                    state.ExitCode = command.ExitCode;
                    return SedFlow.Quit;

                case SedKind.QuitSilent:
                    state.ExitCode = command.ExitCode;
                    return SedFlow.QuitSilent;

                case SedKind.Next:
                    if (!quiet)
                    {
                        output.Append(state.PatternSpace).Append('\n');
                    }

                    Flush(state, output);

                    if (state.Index + 1 >= state.TotalLines)
                    {
                        return SedFlow.QuitSilent;
                    }

                    state.Index++;
                    state.PatternSpace = state.Lines[state.Index];
                    break;

                case SedKind.AppendNext:
                    Flush(state, output);

                    // GNU prints what it has when `N` meets the end of input; POSIX would
                    // discard it, and scripts in the wild rely on the GNU reading.
                    if (state.Index + 1 >= state.TotalLines)
                    {
                        return SedFlow.Quit;
                    }

                    state.Index++;
                    state.PatternSpace = state.PatternSpace + "\n" + state.Lines[state.Index];
                    break;

                case SedKind.Hold:
                    state.HoldSpace = state.PatternSpace;
                    break;

                case SedKind.HoldAppend:
                    state.HoldSpace = state.HoldSpace + "\n" + state.PatternSpace;
                    break;

                case SedKind.Get:
                    state.PatternSpace = state.HoldSpace;
                    break;

                case SedKind.GetAppend:
                    state.PatternSpace = state.PatternSpace + "\n" + state.HoldSpace;
                    break;

                case SedKind.Exchange:
                    (state.PatternSpace, state.HoldSpace) = (state.HoldSpace, state.PatternSpace);
                    break;

                case SedKind.Zap:
                    state.PatternSpace = string.Empty;
                    break;

                //`r' queues the whole file to appear after this cycle's output, the same
                //place `a' text goes - so a missing file is silently nothing, as in GNU.
                case SedKind.ReadFile:
                {
                    var lines = files.LinesOf(command.Text ?? string.Empty);

                    if (lines.Length > 0) state.Appended.Add(string.Join('\n', lines));

                    break;
                }

                //`R' queues one line per execution, and stops producing when the file runs out.
                case SedKind.ReadFileLine:
                {
                    var name = command.Text ?? string.Empty;
                    var lines = files.LinesOf(name);
                    var cursor = files.ReadCursors.TryGetValue(name, out var at) ? at : 0;

                    if (cursor < lines.Length)
                    {
                        state.Appended.Add(lines[cursor]);
                        files.ReadCursors[name] = cursor + 1;
                    }

                    break;
                }

                case SedKind.WriteFile:
                    files.WriterFor(command.Text ?? string.Empty).Append(state.PatternSpace).Append('\n');
                    break;

                case SedKind.WriteFirstLine:
                {
                    var newline = state.PatternSpace.IndexOf('\n', StringComparison.Ordinal);
                    var first = newline < 0 ? state.PatternSpace : state.PatternSpace[..newline];

                    files.WriterFor(command.Text ?? string.Empty).Append(first).Append('\n');
                    break;
                }

                case SedKind.Branch:
                    pc = command.Jump;
                    continue;

                case SedKind.BranchIf:
                    if (state.Substituted)
                    {
                        state.Substituted = false;
                        pc = command.Jump;
                        continue;
                    }

                    break;

                case SedKind.BranchUnless:
                    if (!state.Substituted)
                    {
                        pc = command.Jump;
                        continue;
                    }

                    state.Substituted = false;
                    break;

                case SedKind.Label:
                case SedKind.Block:
                    break;
            }

            pc++;
        }

        return SedFlow.Normal;
    }

    /// <summary>Emits and clears the queued <c>a</c> text.</summary>
    private static void Flush(SedState state, StringBuilder output)
    {
        foreach (var appended in state.Appended)
        {
            output.Append(appended).Append('\n');
        }

        state.Appended.Clear();
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

    /// <summary>What running the script over one pattern space decided to do next.</summary>
    private enum SedFlow
    {
        /// <summary>Fell off the end: print the pattern space and read the next line.</summary>
        Normal,

        /// <summary>Start the next cycle without printing.</summary>
        Deleted,

        /// <summary>Re-run the script over what is left of the pattern space (<c>D</c>).</summary>
        Restart,

        /// <summary>Print the pattern space and stop (<c>q</c>).</summary>
        Quit,

        /// <summary>Stop without printing anything further (<c>Q</c>).</summary>
        QuitSilent,
    }

    /// <summary>The mutable state one input line is processed against.</summary>
    /// <summary>
    /// The files an `r'/`R'/`w'/`W' script touches.
    /// </summary>
    /// <remarks>
    /// Reads are resolved once, before the run, and writes are collected and flushed after
    /// it: the execution loop is synchronous, and threading the filesystem's async surface
    /// through every command to serve four of them would distort the whole evaluator. A file
    /// that changes while sed is running over it is not a case a sandbox needs to model.
    /// </remarks>
    private sealed class SedFiles
    {
        public static readonly SedFiles None = new();

        /// <summary>Lines of each file named by `r' or `R', empty when it could not be read.</summary>
        public Dictionary<string, string[]> Reads { get; } = new(StringComparer.Ordinal);

        /// <summary>How far `R' has consumed each file it reads a line at a time from.</summary>
        public Dictionary<string, int> ReadCursors { get; } = new(StringComparer.Ordinal);

        /// <summary>What `w' and `W' produced, per file, to be written when the run ends.</summary>
        public Dictionary<string, StringBuilder> Writes { get; } = new(StringComparer.Ordinal);

        public string[] LinesOf(string name) => Reads.TryGetValue(name, out var lines) ? lines : [];

        public StringBuilder WriterFor(string name)
        {
            if (!Writes.TryGetValue(name, out var writer))
            {
                writer = new StringBuilder();
                Writes[name] = writer;
            }

            return writer;
        }
    }

    private sealed class SedState(string[] lines)
    {
        public string[] Lines { get; } = lines;

        /// <summary>The zero-based index of the line in the pattern space.</summary>
        public int Index { get; set; }

        public string PatternSpace { get; set; } = string.Empty;

        public string HoldSpace { get; set; } = string.Empty;

        public int LineNumber => Index + 1;

        public int TotalLines => Lines.Length;

        /// <summary>Whether an <c>s</c> succeeded since the last line or <c>t</c>.</summary>
        public bool Substituted { get; set; }

        public int ExitCode { get; set; }

        public List<string> Appended { get; } = [];
    }

    private enum SedKind
    {
        Substitute,
        Delete,
        DeleteFirstLine,
        Print,
        PrintFirstLine,
        PrintLineNumber,
        Append,
        Insert,
        Change,
        Transliterate,
        Quit,
        QuitSilent,
        Next,
        AppendNext,
        Hold,
        HoldAppend,
        Get,
        GetAppend,
        Exchange,
        Zap,
        Label,
        Branch,
        BranchIf,
        BranchUnless,
        Block,
        ReadFile,
        ReadFileLine,
        WriteFile,
        WriteFirstLine,
    }

    private sealed record SedCommand(SedKind Kind, SedAddress Address)
    {
        public Substitution? Substitute { get; init; }

        public string? Text { get; init; }

        public string? From { get; init; }

        public string? To { get; init; }

        /// <summary>Where a branch goes, or where a block's body ends.</summary>
        public int Jump { get; init; }

        public int ExitCode { get; init; }
    }

    /// <summary>A parsed script: a flat instruction list with its jumps already resolved.</summary>
    private sealed class SedProgram(List<SedCommand> commands)
    {
        public List<SedCommand> Commands { get; } = commands;

        /// <summary>Clears the range state, so a fresh input starts outside every range.</summary>
        public void Reset()
        {
            foreach (var command in Commands)
            {
                command.Address.Reset();
            }
        }
    }

    /// <summary>
    /// A command's address: nothing (every line), one line, a step, a pattern, or a range.
    /// A range is stateful — once entered it stays active until its end matches.
    /// </summary>
    private sealed class SedAddress
    {
        public static SedAddress Always { get; } = new();

        public int? Line { get; init; }

        public Regex? Pattern { get; init; }

        public bool Last { get; init; }

        /// <summary>The <c>step</c> of a <c>first~step</c> address.</summary>
        public int? Step { get; init; }

        public int? EndLine { get; init; }

        public Regex? EndPattern { get; init; }

        public bool EndLast { get; init; }

        /// <summary>The <c>N</c> of an <c>addr,+N</c> range.</summary>
        public int? EndRelative { get; init; }

        /// <summary>The <c>N</c> of an <c>addr,~N</c> range.</summary>
        public int? EndMultiple { get; init; }

        public bool IsRange { get; init; }

        public bool Negated { get; init; }

        /// <summary>
        /// True for <c>0,/re/</c>, whose range is open before the first line is read so that
        /// the end pattern can match on line one.
        /// </summary>
        public bool StartsBeforeInput { get; init; }

        /// <summary>True when the last evaluation was this address's final line.</summary>
        public bool JustClosed { get; private set; }

        private bool _inRange;

        private int _rangeStart;

        public void Reset()
        {
            _inRange = StartsBeforeInput;
            _rangeStart = 0;
            JustClosed = false;
        }

        public bool Matches(SedState state)
        {
            var result = Evaluate(state);
            return Negated ? !result : result;
        }

        private bool Evaluate(SedState state)
        {
            JustClosed = !IsRange;

            if (!IsRange)
            {
                return MatchesStart(state);
            }

            if (_inRange)
            {
                if (MatchesEnd(state))
                {
                    _inRange = false;
                    JustClosed = true;
                }

                return true;
            }

            if (!MatchesStart(state))
            {
                return false;
            }

            _rangeStart = state.LineNumber;
            _inRange = !ClosesImmediately(state);
            JustClosed = !_inRange;
            return true;
        }

        private bool ClosesImmediately(SedState state) =>
            (EndLine is { } end && end <= state.LineNumber)
            || EndRelative == 0
            || (EndMultiple is { } multiple && multiple > 1 && state.LineNumber % multiple == 0);

        private bool MatchesStart(SedState state)
        {
            if (Last)
            {
                return state.LineNumber == state.TotalLines;
            }

            if (Step is { } step)
            {
                var first = Line ?? 0;
                return step > 0
                    ? state.LineNumber >= first && (state.LineNumber - first) % step == 0
                    : state.LineNumber == first;
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

            if (EndRelative is { } offset)
            {
                return state.LineNumber >= _rangeStart + offset;
            }

            if (EndMultiple is { } multiple)
            {
                return multiple <= 1 || state.LineNumber % multiple == 0;
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
        public static SedProgram Parse(string script, bool extended)
        {
            var commands = new List<SedCommand>();
            var labels = new Dictionary<string, int>(StringComparer.Ordinal);
            var position = 0;

            ParseBlock(script, ref position, extended, terminator: '\0', commands, labels);

            // Labels may be referenced before they are defined, so branches are resolved
            // once the whole script is known. An empty label means "end of script".
            for (var i = 0; i < commands.Count; i++)
            {
                if (commands[i].Kind is not (SedKind.Branch or SedKind.BranchIf or SedKind.BranchUnless))
                {
                    continue;
                }

                var label = commands[i].Text ?? string.Empty;

                if (label.Length == 0)
                {
                    commands[i] = commands[i] with { Jump = commands.Count };
                    continue;
                }

                if (!labels.TryGetValue(label, out var target))
                {
                    throw new FormatException($"can't find label for jump to `{label}'");
                }

                commands[i] = commands[i] with { Jump = target };
            }

            return new SedProgram(commands);
        }

        private static void ParseBlock(
            string script,
            ref int position,
            bool extended,
            char terminator,
            List<SedCommand> commands,
            Dictionary<string, int> labels)
        {
            while (position < script.Length)
            {
                SkipSeparators(script, ref position);

                if (position >= script.Length)
                {
                    return;
                }

                if (script[position] == terminator)
                {
                    position++;
                    return;
                }

                if (script[position] == '#')
                {
                    while (position < script.Length && script[position] != '\n')
                    {
                        position++;
                    }

                    continue;
                }

                ParseCommand(script, ref position, extended, commands, labels);
            }
        }

        private static void SkipSeparators(string script, ref int position)
        {
            while (position < script.Length && (char.IsWhiteSpace(script[position]) || script[position] == ';'))
            {
                position++;
            }
        }

        private static void ParseCommand(
            string script,
            ref int position,
            bool extended,
            List<SedCommand> commands,
            Dictionary<string, int> labels)
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
                    commands.Add(new SedCommand(SedKind.Substitute, address)
                    {
                        Substitute = ParseSubstitution(script, ref position, extended),
                    });

                    return;

                case 'y':
                {
                    var (from, to) = ParseTransliteration(script, ref position);
                    commands.Add(new SedCommand(SedKind.Transliterate, address) { From = from, To = to });
                    return;
                }

                case 'a' or 'i' or 'c':
                {
                    var text = ParseText(script, ref position);
                    var mapped = kind switch
                    {
                        'a' => SedKind.Append,
                        'i' => SedKind.Insert,
                        _ => SedKind.Change,
                    };

                    commands.Add(new SedCommand(mapped, address) { Text = text });
                    return;
                }

                case 'q' or 'Q':
                    commands.Add(new SedCommand(kind == 'q' ? SedKind.Quit : SedKind.QuitSilent, address)
                    {
                        ExitCode = ReadExitCode(script, ref position),
                    });

                    return;

                case ':':
                {
                    // A label names the instruction that follows it, so nothing is emitted.
                    var name = ReadLabel(script, ref position);

                    if (name.Length == 0)
                    {
                        throw new FormatException("\":\" lacks a label");
                    }

                    labels[name] = commands.Count;
                    return;
                }

                case 'b' or 't' or 'T':
                {
                    var mapped = kind switch
                    {
                        'b' => SedKind.Branch,
                        't' => SedKind.BranchIf,
                        _ => SedKind.BranchUnless,
                    };

                    commands.Add(new SedCommand(mapped, address) { Text = ReadLabel(script, ref position) });
                    return;
                }

                case '{':
                {
                    var index = commands.Count;
                    commands.Add(new SedCommand(SedKind.Block, address));
                    ParseBlock(script, ref position, extended, terminator: '}', commands, labels);
                    commands[index] = commands[index] with { Jump = commands.Count };
                    return;
                }

                //`r'/`R'/`w'/`W' take a filename that runs to the end of the line - a `;' in a
                //path is part of the path, which is why these cannot go through ReadLabel.
                case 'r' or 'R' or 'w' or 'W':
                {
                    var mapped = kind switch
                    {
                        'r' => SedKind.ReadFile,
                        'R' => SedKind.ReadFileLine,
                        'w' => SedKind.WriteFile,
                        _   => SedKind.WriteFirstLine,
                    };

                    var name = ReadFileName(script, ref position);

                    if (name.Length == 0)
                    {
                        throw new FormatException($"missing filename in r/R/w/W commands");
                    }

                    commands.Add(new SedCommand(mapped, address) { Text = name });
                    return;
                }

                default:
                    commands.Add(new SedCommand(Simple(kind), address));
                    return;
            }
        }

        /// <summary>
        /// Reads the filename argument of <c>r</c> / <c>R</c> / <c>w</c> / <c>W</c>.
        /// </summary>
        /// <remarks>
        /// It runs to the end of the line and nothing terminates it early: GNU treats every
        /// remaining character as part of the path, so a script that means to do something
        /// after one has to put it on the next line.
        /// </remarks>
        private static string ReadFileName(string script, ref int position)
        {
            while (position < script.Length && script[position] == ' ')
            {
                position++;
            }

            var start = position;

            while (position < script.Length && script[position] != '\n')
            {
                position++;
            }

            return script[start..position].TrimEnd();
        }

        /// <summary>Maps a command letter that carries no argument.</summary>
        private static SedKind Simple(char kind) => kind switch
        {
            'd' => SedKind.Delete,
            'D' => SedKind.DeleteFirstLine,
            'p' => SedKind.Print,
            'P' => SedKind.PrintFirstLine,
            '=' => SedKind.PrintLineNumber,
            'n' => SedKind.Next,
            'N' => SedKind.AppendNext,
            'h' => SedKind.Hold,
            'H' => SedKind.HoldAppend,
            'g' => SedKind.Get,
            'G' => SedKind.GetAppend,
            'x' => SedKind.Exchange,
            'z' => SedKind.Zap,
            _ => throw new FormatException($"unknown command: `{kind}'"),
        };

        private static int ReadExitCode(string script, ref int position)
        {
            while (position < script.Length && script[position] == ' ')
            {
                position++;
            }

            var start = position;

            while (position < script.Length && char.IsAsciiDigit(script[position]))
            {
                position++;
            }

            return position == start ? 0 : int.Parse(script[start..position], CultureInfo.InvariantCulture);
        }

        /// <summary>Reads a label name, which runs to the end of the command.</summary>
        private static string ReadLabel(string script, ref int position)
        {
            while (position < script.Length && script[position] == ' ')
            {
                position++;
            }

            var start = position;

            while (position < script.Length && script[position] is not (';' or '\n' or '}'))
            {
                position++;
            }

            return script[start..position].TrimEnd();
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

            var start = ParseOneAddress(script, ref position, extended, isEnd: false);

            if (!start.Found)
            {
                return SedAddress.Always;
            }

            if (position < script.Length && script[position] == ',')
            {
                position++;
                var end = ParseOneAddress(script, ref position, extended, isEnd: true);

                return new SedAddress
                {
                    Line = start.Line,
                    Pattern = start.Pattern,
                    Last = start.Last,
                    Step = start.Step,
                    EndLine = end.Line,
                    EndPattern = end.Pattern,
                    EndLast = end.Last,
                    EndRelative = end.Relative,
                    EndMultiple = end.Multiple,
                    IsRange = true,

                    // `0,/re/` is GNU's way of saying "up to the first match, even on line 1".
                    StartsBeforeInput = start.Line == 0 && start.Step is null,
                    Negated = ConsumeNegation(script, ref position),
                };
            }

            return new SedAddress
            {
                Line = start.Line,
                Pattern = start.Pattern,
                Last = start.Last,
                Step = start.Step,
                Negated = ConsumeNegation(script, ref position),
            };
        }

        private static bool ConsumeNegation(string script, ref int position)
        {
            var negated = false;

            while (position < script.Length && (script[position] == ' ' || script[position] == '!'))
            {
                if (script[position] == '!')
                {
                    negated = !negated;
                }

                position++;
            }

            return negated;
        }

        /// <summary>One half of an address specification.</summary>
        private readonly record struct AddressPart(
            int? Line,
            Regex? Pattern,
            bool Last,
            int? Step,
            int? Relative,
            int? Multiple,
            bool Found);

        private static AddressPart ParseOneAddress(string script, ref int position, bool extended, bool isEnd)
        {
            while (position < script.Length && script[position] == ' ')
            {
                position++;
            }

            if (position >= script.Length)
            {
                return default;
            }

            if (script[position] == '$')
            {
                position++;
                return new AddressPart(null, null, true, null, null, null, true);
            }

            // `addr,+N` and `addr,~N` are relative ends and only make sense as the second half.
            if (isEnd && script[position] is '+' or '~')
            {
                var relative = script[position] == '+';
                position++;
                var count = ReadNumber(script, ref position) ?? 0;

                return relative
                    ? new AddressPart(null, null, false, null, count, null, true)
                    : new AddressPart(null, null, false, null, null, count, true);
            }

            if (char.IsAsciiDigit(script[position]))
            {
                var line = ReadNumber(script, ref position) ?? 0;

                // `first~step` selects every step'th line from `first`.
                if (!isEnd && position < script.Length && script[position] == '~')
                {
                    position++;
                    var step = ReadNumber(script, ref position) ?? 0;
                    return new AddressPart(line, null, false, step, null, null, true);
                }

                return new AddressPart(line, null, false, null, null, null, true);
            }

            if (script[position] == '/')
            {
                position++;
                var text = ReadDelimited(script, ref position, '/');
                var pattern = CompileRegex(text, extended, ReadRegexFlags(script, ref position));
                return new AddressPart(null, pattern, false, null, null, null, true);
            }

            // `\cREGEXc` allows any delimiter.
            if (script[position] == '\\' && position + 1 < script.Length)
            {
                var delimiter = script[position + 1];
                position += 2;
                var text = ReadDelimited(script, ref position, delimiter);
                var pattern = CompileRegex(text, extended, ReadRegexFlags(script, ref position));
                return new AddressPart(null, pattern, false, null, null, null, true);
            }

            return default;
        }

        private static int? ReadNumber(string script, ref int position)
        {
            var start = position;

            while (position < script.Length && char.IsAsciiDigit(script[position]))
            {
                position++;
            }

            return position == start ? null : int.Parse(script[start..position], CultureInfo.InvariantCulture);
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
            var multiline = false;
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

                if (c is 'm' or 'M')
                {
                    multiline = true;
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

            return new Substitution(CompileRegex(pattern, extended, ignoreCase, multiline), replacement, global, occurrence, print);
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

        private static Regex CompileRegex(string pattern, bool extended, bool ignoreCase, bool multiline = false)
        {
            var translated = PosixRegex.Translate(pattern, extended);
            var options = RegexOptions.None;

            if (ignoreCase) options |= RegexOptions.IgnoreCase;

            // GNU's `M' flag: ^ and $ match at every embedded newline rather than only at the
            // ends of the pattern space. It only means anything once N has joined lines into
            // one pattern space, which is exactly when scripts reach for it.
            if (multiline) options |= RegexOptions.Multiline;

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
