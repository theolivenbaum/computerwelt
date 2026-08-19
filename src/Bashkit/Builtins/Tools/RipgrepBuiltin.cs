using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bashkit.Builtins;

/// <summary>
/// <c>rg</c> — ripgrep's interface over the same search.
/// </summary>
/// <remarks>
/// Not an alias for <c>grep</c>: the defaults differ in ways scripts depend on. Patterns are
/// Rust-flavoured regexes rather than POSIX EREs, and line numbers appear only when
/// requested — ripgrep shows them for a terminal, and a pipe is not one.
/// </remarks>
public sealed class RipgrepBuiltin : IBuiltin
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public string Name => "rg";

    /// <inheritdoc />
    public string? LlmHint =>
        "rg: Recursive regex search. Supports -i, -n/-N, -c, -v, -F, -w, -m, -l, -q, -e.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: rg [OPTIONS] PATTERN [PATH...]
        Recursively search for PATTERN.

          -i    case insensitive
          -n    show line numbers
          -N    hide line numbers
          -c    show a count of matching lines
          -v    invert the match
          -F    treat the pattern as a literal string
          -w    match whole words only
          -m N  stop after N matching lines per file
          -l    show only the names of matching files
          -q    print nothing; the exit status reports the match
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var ignoreCase = false;
        var lineNumbers = false;
        var count = false;
        var invert = false;
        var literal = false;
        var wholeWord = false;
        var namesOnly = false;
        var quiet = false;
        var maxCount = 0;
        string? pattern = null;
        var operands = new List<string>();

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            var argument = context.Arguments[i];

            switch (argument)
            {
                case "--help": return ExecResult.Ok(Help + "\n");
                case "--version": return ExecResult.Ok("ripgrep (bashkit) 0.1\n");
                case "--ignore-case" or "-i": ignoreCase = true; continue;
                case "--line-number": lineNumbers = true; continue;
                case "--no-line-number": lineNumbers = false; continue;
                case "--count": count = true; continue;
                case "--invert-match": invert = true; continue;
                case "--fixed-strings": literal = true; continue;
                case "--word-regexp": wholeWord = true; continue;
                case "--files-with-matches": namesOnly = true; continue;
                case "--quiet": quiet = true; continue;

                case "--max-count" when i + 1 < context.Arguments.Count:
                    maxCount = int.TryParse(context.Arguments[++i], CultureInfo.InvariantCulture, out var m) ? m : 0;
                    continue;

                case "-e" or "--regexp" when i + 1 < context.Arguments.Count:
                    pattern = context.Arguments[++i];
                    continue;
            }

            if (argument.Length > 1 && argument[0] == '-' && !argument.StartsWith("--", StringComparison.Ordinal))
            {
                for (var c = 1; c < argument.Length; c++)
                {
                    switch (argument[c])
                    {
                        case 'i': ignoreCase = true; break;
                        case 'n': lineNumbers = true; break;
                        case 'N': lineNumbers = false; break;
                        case 'c': count = true; break;
                        case 'v': invert = true; break;
                        case 'F': literal = true; break;
                        case 'w': wholeWord = true; break;
                        case 'l': namesOnly = true; break;
                        case 'q': quiet = true; break;
                        case 'a' or 'S' or 'u' or 'H': break;

                        case 'm':
                        {
                            var text = c + 1 < argument.Length ? argument[(c + 1)..]
                                : i + 1 < context.Arguments.Count ? context.Arguments[++i] : null;

                            maxCount = int.TryParse(text, CultureInfo.InvariantCulture, out var value) ? value : 0;
                            c = argument.Length;
                            break;
                        }

                        default:
                            return ExecResult.Usage(Name, $"unrecognized flag -{argument[c]}");
                    }
                }

                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                return ExecResult.Usage(Name, $"unrecognized flag {argument}");
            }

            if (pattern is null)
            {
                pattern = argument;
                continue;
            }

            operands.Add(argument);
        }

        if (pattern is null)
        {
            return ExecResult.Usage(Name, "missing PATTERN");
        }

        Regex regex;

        try
        {
            var source = literal ? Regex.Escape(pattern) : pattern;
            source = wholeWord ? $@"\b(?:{source})\b" : source;
            regex = new Regex(source, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, MatchTimeout);
        }
        catch (ArgumentException exception)
        {
            return ExecResult.Error($"rg: regex parse error: {exception.Message}\n", ExitCodes.Usage);
        }

        var files = await context.ReadOperandsAsync(operands, cancellationToken);
        var output = new StringBuilder();
        var showNames = files.Count > 1;
        var matched = false;

        foreach (var (name, content) in files)
        {
            var lines = content.Length == 0
                ? []
                : (content.EndsWith('\n') ? content[..^1] : content).Split('\n');

            var hits = 0;

            for (var i = 0; i < lines.Length; i++)
            {
                context.Budget.ChargeWork(1);

                if (regex.IsMatch(lines[i]) == invert)
                {
                    continue;
                }

                matched = true;
                hits++;

                if (maxCount > 0 && hits > maxCount)
                {
                    hits = maxCount;
                    break;
                }

                if (quiet || count || namesOnly)
                {
                    continue;
                }

                if (showNames)
                {
                    output.Append(name).Append(':');
                }

                if (lineNumbers)
                {
                    output.Append(i + 1).Append(':');
                }

                output.Append(lines[i]).Append('\n');
            }

            if (count)
            {
                output.Append(showNames ? name + ":" : string.Empty).Append(hits).Append('\n');
            }
            else if (namesOnly && hits > 0)
            {
                output.Append(name).Append('\n');
            }
        }

        return new ExecResult
        {
            Stdout = StreamData.FromText(quiet ? string.Empty : output.ToString()),
            ExitCode = matched ? ExitCodes.Success : ExitCodes.Failure,
        };
    }
}
