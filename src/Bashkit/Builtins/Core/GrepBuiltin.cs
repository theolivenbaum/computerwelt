using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bashkit.Builtins;

/// <summary>
/// <c>grep</c>, <c>egrep</c> and <c>fgrep</c> — prints lines matching a pattern.
/// </summary>
/// <remarks>
/// <para>
/// Three pattern dialects have to coexist: basic regular expressions (the default, where
/// <c>\(</c> groups and a bare <c>(</c> is literal), extended ones (<c>-E</c>, which is
/// essentially .NET's syntax), and fixed strings (<c>-F</c>). Rather than three matchers,
/// each dialect is translated into a .NET regex, with BRE handled by
/// <see cref="BasicRegex"/> and fixed strings by escaping.
/// </para>
/// <para>
/// Every match runs under a timeout: patterns come from the script, and a catastrophically
/// backtracking one must not become a way to hang the sandbox.
/// </para>
/// </remarks>
public sealed class GrepBuiltin : IBuiltin
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Creates the builtin under <paramref name="name"/>: grep, egrep or fgrep.</summary>
    public GrepBuiltin(string name = "grep") => Name = name;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string? LlmHint =>
        "grep: Searches text. Supports -i -v -n -c -l -L -w -x -E -F -r -A -B -C -o -q -e -f --include --exclude.";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var options = new GrepOptions
        {
            Extended = Name == "egrep",
            Fixed = Name == "fgrep",
        };

        var cursor = new ArgCursor(context.Arguments);
        var patterns = new List<string>();
        var patternFiles = new List<string>();

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-i" or "-y" or "--ignore-case": options.IgnoreCase = true; break;
                case "-v" or "--invert-match": options.Invert = true; break;
                case "-n" or "--line-number": options.LineNumbers = true; break;
                case "-c" or "--count": options.CountOnly = true; break;
                case "-l" or "--files-with-matches": options.NamesOnly = true; break;
                case "-L" or "--files-without-match": options.NamesWithoutMatch = true; break;
                case "-h" or "--no-filename": options.SuppressNames = true; break;
                case "-H" or "--with-filename": options.ForceNames = true; break;
                case "-w" or "--word-regexp": options.WholeWord = true; break;
                case "-x" or "--line-regexp": options.WholeLine = true; break;
                case "-E" or "--extended-regexp": options.Extended = true; break;
                case "-F" or "--fixed-strings": options.Fixed = true; break;
                case "-G" or "--basic-regexp": options.Extended = false; options.Fixed = false; break;
                case "-r" or "-R" or "--recursive": options.Recursive = true; break;
                case "-o" or "--only-matching": options.OnlyMatching = true; break;
                case "-q" or "--quiet" or "--silent": options.Quiet = true; break;
                case "-s" or "--no-messages": options.NoMessages = true; break;
                case "-a" or "--text" or "--color" or "--colour" or "-I" or "-b" or "-Z" or "-z": break;

                case "-e" or "--regexp":
                    if (cursor.TakeValue() is { } pattern)
                    {
                        patterns.Add(pattern);
                    }

                    break;

                case "-f" or "--file":
                    if (cursor.TakeValue() is { } patternFile)
                    {
                        patternFiles.Add(patternFile);
                    }

                    break;

                case "-A" or "--after-context":
                    int.TryParse(cursor.TakeValue(), CultureInfo.InvariantCulture, out options.After);
                    break;

                case "-B" or "--before-context":
                    int.TryParse(cursor.TakeValue(), CultureInfo.InvariantCulture, out options.Before);
                    break;

                case "-C" or "--context":
                {
                    int.TryParse(cursor.TakeValue(), CultureInfo.InvariantCulture, out var context_);
                    options.After = context_;
                    options.Before = context_;
                    break;
                }

                case "-m" or "--max-count":
                    int.TryParse(cursor.TakeValue(), CultureInfo.InvariantCulture, out options.MaxCount);
                    break;

                case "--include": options.Include = cursor.TakeValue(); break;
                case "--exclude": options.Exclude = cursor.TakeValue(); break;

                default:
                    // `grep -5` is shorthand for `-C 5`.
                    if (option.Length > 1 && char.IsAsciiDigit(option[1]))
                    {
                        options.After = options.Before = int.Parse(option[1..], CultureInfo.InvariantCulture);
                        break;
                    }

                    return ExecResult.Usage("grep", $"invalid option -- '{option.TrimStart('-')}'", ExitCodes.Usage);
            }
        }

        var operands = cursor.Operands;

        // With no `-e`/`-f`, the first operand is the pattern.
        if (patterns.Count == 0 && patternFiles.Count == 0)
        {
            if (operands.Count == 0)
            {
                return ExecResult.Usage("grep", "usage: grep [OPTION]... PATTERN [FILE]...", ExitCodes.Usage);
            }

            patterns.Add(operands[0]);
            operands = operands[1..];
        }

        foreach (var file in patternFiles)
        {
            try
            {
                var bytes = await context.FileSystem.ReadFileAsync(context.ResolvePath(file), cancellationToken);
                patterns.AddRange(TextHelpers.SplitLines(Encoding.UTF8.GetString(bytes)));
            }
            catch (FileSystemException)
            {
                return ExecResult.Error($"grep: {file}: No such file or directory\n", ExitCodes.Usage);
            }
        }

        Regex regex;
        try
        {
            regex = Compile(patterns, options);
        }
        catch (ArgumentException e)
        {
            return ExecResult.Usage("grep", e.Message, ExitCodes.Usage);
        }

        var targets = await ResolveTargetsAsync(context, operands, options, cancellationToken);
        var showNames = options.ForceNames || (!options.SuppressNames && (targets.Count > 1 || options.Recursive));

        var builder = new StringBuilder();
        var errors = new StringBuilder();
        var anyMatch = false;
        var anyError = false;

        foreach (var (label, path) in targets)
        {
            string content;

            if (path is null)
            {
                content = context.StdinText;
            }
            else
            {
                try
                {
                    content = Encoding.UTF8.GetString(await context.FileSystem.ReadFileAsync(path.Value, cancellationToken));
                }
                catch (FileSystemException)
                {
                    anyError = true;
                    if (!options.NoMessages)
                    {
                        errors.Append("grep: ").Append(label).Append(": No such file or directory\n");
                    }

                    continue;
                }
            }

            var matched = Search(regex, content, label, showNames, options, builder);
            anyMatch |= matched;

            if (matched && options.Quiet)
            {
                return ExecResult.Success;
            }
        }

        if (options.Quiet)
        {
            return ExecResult.FromExitCode(anyMatch ? 0 : 1);
        }

        return new ExecResult
        {
            Stdout = StreamData.FromText(builder.ToString()),
            Stderr = StreamData.FromText(errors.ToString()),
            // 0 when something matched, 1 when nothing did, 2 on an error.
            ExitCode = anyError && !anyMatch ? ExitCodes.Usage : anyMatch ? 0 : 1,
        };
    }

    private static bool Search(Regex regex, string content, string label, bool showNames, GrepOptions options, StringBuilder builder)
    {
        var lines = TextHelpers.SplitLines(content);
        var matches = new List<int>();

        for (var i = 0; i < lines.Length; i++)
        {
            bool isMatch;
            try
            {
                isMatch = regex.IsMatch(lines[i]);
            }
            catch (RegexMatchTimeoutException)
            {
                isMatch = false;
            }

            if (isMatch != options.Invert)
            {
                matches.Add(i);
                if (options.MaxCount > 0 && matches.Count >= options.MaxCount)
                {
                    break;
                }
            }
        }

        if (options.CountOnly)
        {
            if (showNames)
            {
                builder.Append(label).Append(':');
            }

            builder.Append(matches.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return matches.Count > 0;
        }

        if (options.NamesOnly)
        {
            if (matches.Count > 0)
            {
                builder.Append(label).Append('\n');
            }

            return matches.Count > 0;
        }

        if (options.NamesWithoutMatch)
        {
            if (matches.Count == 0)
            {
                builder.Append(label).Append('\n');
            }

            return matches.Count > 0;
        }

        if (options.Quiet)
        {
            return matches.Count > 0;
        }

        // Context lines are emitted with `-` instead of `:` after the name, and runs of
        // context are separated by `--`, exactly as GNU grep does.
        var emitted = new SortedSet<int>();
        foreach (var index in matches)
        {
            for (var i = Math.Max(0, index - options.Before); i <= Math.Min(lines.Length - 1, index + options.After); i++)
            {
                emitted.Add(i);
            }
        }

        var previous = -2;
        var hasContext = options.Before > 0 || options.After > 0;

        foreach (var index in emitted)
        {
            if (hasContext && previous >= 0 && index > previous + 1)
            {
                builder.Append("--\n");
            }

            previous = index;
            var isMatchLine = matches.Contains(index);
            var separator = isMatchLine ? ':' : '-';

            if (options.OnlyMatching)
            {
                if (!isMatchLine)
                {
                    continue;
                }

                foreach (Match match in regex.Matches(lines[index]))
                {
                    AppendPrefix(builder, label, showNames, index, separator, options);
                    builder.Append(match.Value).Append('\n');
                }

                continue;
            }

            AppendPrefix(builder, label, showNames, index, separator, options);
            builder.Append(lines[index]).Append('\n');
        }

        return matches.Count > 0;
    }

    private static void AppendPrefix(StringBuilder builder, string label, bool showNames, int index, char separator, GrepOptions options)
    {
        if (showNames)
        {
            builder.Append(label).Append(separator);
        }

        if (options.LineNumbers)
        {
            builder.Append((index + 1).ToString(CultureInfo.InvariantCulture)).Append(separator);
        }
    }

    private static Regex Compile(List<string> patterns, GrepOptions options)
    {
        var alternatives = new List<string>(patterns.Count);

        foreach (var pattern in patterns)
        {
            // `-e 'a\nb'` and `-f file` may carry several patterns per entry.
            foreach (var line in pattern.Split('\n'))
            {
                var translated = options.Fixed
                    ? Regex.Escape(line)
                    : options.Extended ? line : BasicRegex.Translate(line);

                if (options.WholeLine)
                {
                    translated = "^(?:" + translated + ")$";
                }
                else if (options.WholeWord)
                {
                    translated = @"\b(?:" + translated + @")\b";
                }

                alternatives.Add(translated);
            }
        }

        var combined = string.Join('|', alternatives.Select(static a => "(?:" + a + ")"));
        var regexOptions = options.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;

        return new Regex(combined, regexOptions, MatchTimeout);
    }

    private static async ValueTask<List<(string Label, VPath? Path)>> ResolveTargetsAsync(
        BuiltinContext context,
        IReadOnlyList<string> operands,
        GrepOptions options,
        CancellationToken cancellationToken)
    {
        var targets = new List<(string, VPath?)>();

        if (operands.Count == 0)
        {
            targets.Add(("(standard input)", null));
            return targets;
        }

        foreach (var operand in operands)
        {
            if (operand == "-")
            {
                targets.Add(("(standard input)", null));
                continue;
            }

            var path = context.ResolvePath(operand);

            if (!options.Recursive)
            {
                targets.Add((operand, path));
                continue;
            }

            await CollectAsync(context, path, operand, targets, options, cancellationToken);
        }

        return targets;
    }

    private static async ValueTask CollectAsync(
        BuiltinContext context,
        VPath path,
        string label,
        List<(string, VPath?)> targets,
        GrepOptions options,
        CancellationToken cancellationToken)
    {
        FileMetadata metadata;
        try
        {
            metadata = await context.FileSystem.StatAsync(path, cancellationToken);
        }
        catch (FileSystemException)
        {
            targets.Add((label, path));
            return;
        }

        if (!metadata.IsDirectory)
        {
            if (Included(path.FileName, options))
            {
                targets.Add((label, path));
            }

            return;
        }

        foreach (var entry in await context.FileSystem.ReadDirectoryAsync(path, cancellationToken))
        {
            await CollectAsync(context, path.Join(entry.Name), label + "/" + entry.Name, targets, options, cancellationToken);
        }
    }

    private static bool Included(string name, GrepOptions options)
    {
        if (options.Include is { } include && !Interpreter.PatternMatcher.IsMatch(name, include))
        {
            return false;
        }

        return options.Exclude is not { } exclude || !Interpreter.PatternMatcher.IsMatch(name, exclude);
    }

    private sealed class GrepOptions
    {
        public bool IgnoreCase;
        public bool Invert;
        public bool LineNumbers;
        public bool CountOnly;
        public bool NamesOnly;
        public bool NamesWithoutMatch;
        public bool SuppressNames;
        public bool ForceNames;
        public bool WholeWord;
        public bool WholeLine;
        public bool Extended;
        public bool Fixed;
        public bool Recursive;
        public bool OnlyMatching;
        public bool Quiet;
        public bool NoMessages;
        public int After;
        public int Before;
        public int MaxCount;
        public string? Include;
        public string? Exclude;
    }
}
