using System.Globalization;
using System.Text;
using Bashkit.Interpreter;

namespace Bashkit.Builtins;

/// <summary><c>basename</c> — strips directory components and an optional suffix.</summary>
public sealed class BasenameBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "basename";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        string? suffix = null;
        var multiple = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-s" or "--suffix":
                    suffix = cursor.TakeValue();
                    multiple = true;
                    break;

                case "-a" or "--multiple": multiple = true; break;
                case "-z" or "--zero": break;
                default:
                    return ValueTask.FromResult(ExecResult.Usage("basename", $"invalid option -- '{option.TrimStart('-')}'"));
            }
        }

        if (cursor.Operands.Count == 0)
        {
            return ValueTask.FromResult(ExecResult.Usage("basename", "missing operand", ExitCodes.Failure));
        }

        // `basename path suffix` is the two-operand form; `-a`/`-s` switch to the list form.
        if (!multiple && cursor.Operands.Count == 2)
        {
            suffix = cursor.Operands[1];
            cursor.Operands.RemoveAt(1);
        }

        var builder = new StringBuilder();
        foreach (var operand in cursor.Operands)
        {
            builder.Append(Strip(operand, suffix)).Append('\n');
        }

        return ValueTask.FromResult(ExecResult.Ok(builder.ToString()));
    }

    private static string Strip(string path, string? suffix)
    {
        var trimmed = path.TrimEnd('/');

        if (trimmed.Length == 0)
        {
            return path.Length > 0 ? "/" : string.Empty;
        }

        var index = trimmed.LastIndexOf('/');
        var name = index < 0 ? trimmed : trimmed[(index + 1)..];

        // A suffix is only removed when it is not the entire name.
        if (suffix is { Length: > 0 } && name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
        {
            name = name[..^suffix.Length];
        }

        return name;
    }
}

/// <summary><c>dirname</c> — strips the last path component.</summary>
public sealed class DirnameBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "dirname";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);

        while (cursor.NextOption() is { } option)
        {
            if (option is "-z" or "--zero")
            {
                continue;
            }

            return ValueTask.FromResult(ExecResult.Usage("dirname", $"invalid option -- '{option.TrimStart('-')}'"));
        }

        if (cursor.Operands.Count == 0)
        {
            return ValueTask.FromResult(ExecResult.Usage("dirname", "missing operand", ExitCodes.Failure));
        }

        var builder = new StringBuilder();
        foreach (var operand in cursor.Operands)
        {
            builder.Append(Directory(operand)).Append('\n');
        }

        return ValueTask.FromResult(ExecResult.Ok(builder.ToString()));
    }

    private static string Directory(string path)
    {
        var trimmed = path.TrimEnd('/');

        if (trimmed.Length == 0)
        {
            return path.StartsWith('/') ? "/" : ".";
        }

        var index = trimmed.LastIndexOf('/');

        return index switch
        {
            < 0 => ".",
            0 => "/",
            _ => trimmed[..index],
        };
    }
}

/// <summary><c>env</c> and <c>printenv</c> — print the environment.</summary>
public sealed class EnvBuiltin : IBuiltin
{
    /// <summary>Creates the builtin under <paramref name="name"/>.</summary>
    public EnvBuiltin(string name = "env") => Name = name;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        // The reported environment is what the script itself exported. The shell's own
        // defaults are inherited by child shells but are not part of it, so a fresh shell
        // reports nothing — the same as a process started with an empty environ.
        var environment = context.State.ExportedEnvironment()
            .Where(entry => !context.State.ShellDefaults.Contains(entry.Key))
            .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);

        if (Name == "env")
        {
            return ValueTask.FromResult(RunEnv(context, environment));
        }

        // `printenv NAME` prints one value and fails when it is unset.
        if (Name == "printenv" && context.Arguments.Count > 0)
        {
            var builder = new StringBuilder();
            var missing = false;

            foreach (var name in context.Arguments)
            {
                if (environment.TryGetValue(name, out var value))
                {
                    builder.Append(value).Append('\n');
                }
                else
                {
                    missing = true;
                }
            }

            return ValueTask.FromResult(new ExecResult
            {
                Stdout = StreamData.FromText(builder.ToString()),
                ExitCode = missing ? 1 : 0,
            });
        }

        var output = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            output.Append(name).Append('=').Append(value).Append('\n');
        }

        return ValueTask.FromResult(ExecResult.Ok(output.ToString()));
    }

    /// <summary>
    /// Runs <c>env</c>, which prints the environment its own options describe.
    /// </summary>
    /// <remarks>
    /// Running a command through <c>env</c> is not supported: it would need a process, and
    /// the shell already offers the same effect with a temporary assignment prefix.
    /// </remarks>
    private static ExecResult RunEnv(BuiltinContext context, Dictionary<string, string> environment)
    {
        var result = new Dictionary<string, string>(environment, StringComparer.Ordinal);

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            var argument = context.Arguments[i];

            switch (argument)
            {
                case "-i" or "--ignore-environment" or "-":
                    result.Clear();
                    continue;

                case "-0" or "--null":
                    continue;

                case "-u" or "--unset" when i + 1 < context.Arguments.Count:
                    result.Remove(context.Arguments[++i]);
                    continue;
            }

            var equals = argument.IndexOf('=', StringComparison.Ordinal);

            if (equals > 0)
            {
                result[argument[..equals]] = argument[(equals + 1)..];
                continue;
            }

            if (!argument.StartsWith('-'))
            {
                return ExecResult.Error($"env: '{argument}': running commands is not supported\n", 127);
            }
        }

        var output = new StringBuilder();

        foreach (var (name, value) in result.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            output.Append(name).Append('=').Append(value).Append('\n');
        }

        return ExecResult.Ok(output.ToString());
    }
}

/// <summary><c>read</c> — reads a line from standard input into variables.</summary>
public sealed class ReadBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "read";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var rawMode = false;
        string? arrayName = null;
        int? maxChars = null;
        var delimiter = '\n';

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-r": rawMode = true; break;
                case "-a": arrayName = cursor.TakeValue(); break;
                case "-d":
                {
                    var value = cursor.TakeValue();
                    delimiter = value is { Length: > 0 } ? value[0] : '\0';
                    break;
                }

                case "-n" or "-N":
                    if (int.TryParse(cursor.TakeValue(), CultureInfo.InvariantCulture, out var chars))
                    {
                        maxChars = chars;
                    }

                    break;

                case "-p" or "-t" or "-u": cursor.TakeValue(); break;
                case "-s" or "-e": break;
                default:
                    return ValueTask.FromResult(ExecResult.Usage("read", $"invalid option -- '{option.TrimStart('-')}'"));
            }
        }

        // Reading consumes, so a loop over the same input advances rather than repeating.
        string? line;
        bool terminated;

        if (context.Input is { } stream)
        {
            line = stream.ReadLine(delimiter, out terminated);
        }
        else
        {
            var input = context.StdinText;
            var newline = input.IndexOf(delimiter, StringComparison.Ordinal);
            line = input.Length == 0 ? null : newline < 0 ? input : input[..newline];
            terminated = newline >= 0;
        }

        var names = cursor.Operands;

        if (line is null)
        {
            // At end of input the variables are cleared, which is what lets
            // `while read line || [[ -n "$line" ]]` terminate instead of repeating.
            foreach (var name in names)
            {
                context.State.Set(name, string.Empty);
            }

            if (arrayName is not null)
            {
                context.State.GetOrCreate(arrayName).SetArray([]);
            }

            if (names.Count == 0)
            {
                context.State.Set("REPLY", string.Empty);
            }

            return ValueTask.FromResult(ExecResult.FromExitCode(1));
        }

        if (maxChars is { } limit && line.Length > limit)
        {
            line = line[..limit];
        }

        if (!rawMode)
        {
            line = RemoveBackslashes(line);
        }

        // A final line with no delimiter is assigned but still reports failure.
        var status = terminated ? 0 : 1;

        if (arrayName is not null)
        {
            var variable = context.State.GetOrCreate(arrayName);
            variable.SetArray(SplitFields(line, context.State.Ifs));
            return ValueTask.FromResult(ExecResult.FromExitCode(status));
        }

        if (names.Count == 0)
        {
            context.State.Set("REPLY", line);
            return ValueTask.FromResult(ExecResult.FromExitCode(status));
        }

        foreach (var (name, value) in names.Zip(SplitForNames(line, context.State.Ifs, names.Count)))
        {
            context.State.Set(name, value);
        }

        return ValueTask.FromResult(ExecResult.FromExitCode(status));
    }

    /// <summary>Splits a line into every field it holds, for <c>read -a</c>.</summary>
    private static List<string> SplitFields(string line, string ifs)
    {
        if (ifs.Length == 0 || line.Length == 0)
        {
            return line.Length == 0 ? [] : [line];
        }

        // Asking for one more field than the line can hold yields them all, because the
        // last one then has nothing left to absorb.
        var fields = SplitForNames(line, ifs, line.Length + 1);

        while (fields.Count > 0 && fields[^1].Length == 0)
        {
            fields.RemoveAt(fields.Count - 1);
        }

        return fields;
    }

    /// <summary>
    /// Splits a line into exactly <paramref name="count"/> fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not word splitting. <c>read</c> distinguishes the two kinds of <c>IFS</c>
    /// character: a run of IFS <i>whitespace</i> is one delimiter, while an IFS
    /// non-whitespace character always delimits, so <c>IFS=: read a b c d</c> over
    /// <c>one::three:</c> finds an empty second field rather than collapsing it away.
    /// </para>
    /// <para>
    /// The last variable takes the whole remainder, delimiters included, with only trailing
    /// IFS whitespace removed — which is why <c>IFS=,: read a b</c> over <c>1,2:3</c>
    /// leaves <c>b</c> holding <c>2:3</c>.
    /// </para>
    /// </remarks>
    private static List<string> SplitForNames(string line, string ifs, int count)
    {
        var fields = new List<string>(count);

        if (ifs.Length == 0)
        {
            fields.Add(line);

            while (fields.Count < count)
            {
                fields.Add(string.Empty);
            }

            return fields;
        }

        bool IsWhitespace(char c) => char.IsWhiteSpace(c) && ifs.Contains(c, StringComparison.Ordinal);
        bool IsSeparator(char c) => ifs.Contains(c, StringComparison.Ordinal);

        var position = 0;

        while (position < line.Length && IsWhitespace(line[position]))
        {
            position++;
        }

        while (fields.Count < count - 1)
        {
            if (position >= line.Length)
            {
                fields.Add(string.Empty);
                continue;
            }

            var start = position;

            while (position < line.Length && !IsSeparator(line[position]))
            {
                position++;
            }

            fields.Add(line[start..position]);

            // A delimiter is optional IFS whitespace around at most one non-whitespace
            // IFS character.
            while (position < line.Length && IsWhitespace(line[position]))
            {
                position++;
            }

            if (position < line.Length && IsSeparator(line[position]))
            {
                position++;

                while (position < line.Length && IsWhitespace(line[position]))
                {
                    position++;
                }
            }
        }

        var rest = position < line.Length ? line[position..] : string.Empty;

        while (rest.Length > 0 && IsWhitespace(rest[^1]))
        {
            rest = rest[..^1];
        }

        fields.Add(rest);
        return fields;
    }

    private static string RemoveBackslashes(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                builder.Append(text[++i]);
                continue;
            }

            builder.Append(text[i]);
        }

        return builder.ToString();
    }
}

/// <summary><c>eval</c> — runs its arguments as a script in the current shell.</summary>
public sealed class EvalBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "eval";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (context.Arguments.Count == 0)
        {
            return ValueTask.FromResult(ExecResult.Success);
        }

        // The arguments are joined with spaces and re-parsed. They have already been
        // expanded once, so `eval` performs a deliberate second round — which is the whole
        // point of it, and the reason it is dangerous outside a sandbox.
        return context.Hooks.RunFragment(string.Join(' ', context.Arguments), context.Stdin, cancellationToken);
    }
}

/// <summary><c>alias</c> — defines or lists command aliases.</summary>
public sealed class AliasBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "alias";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (context.Arguments.Count == 0)
        {
            var builder = new StringBuilder();
            foreach (var (name, value) in context.State.Aliases.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                builder.Append("alias ").Append(name).Append("='").Append(value).Append("'\n");
            }

            return ValueTask.FromResult(ExecResult.Ok(builder.ToString()));
        }

        var output = new StringBuilder();
        var missing = false;

        foreach (var argument in context.Arguments)
        {
            var equals = argument.IndexOf('=', StringComparison.Ordinal);

            if (equals < 0)
            {
                if (context.State.Aliases.TryGetValue(argument, out var value))
                {
                    output.Append("alias ").Append(argument).Append("='").Append(value).Append("'\n");
                }
                else
                {
                    missing = true;
                }

                continue;
            }

            context.State.Aliases[argument[..equals]] = argument[(equals + 1)..];
        }

        return ValueTask.FromResult(new ExecResult
        {
            Stdout = StreamData.FromText(output.ToString()),
            ExitCode = missing ? 1 : 0,
        });
    }
}

/// <summary><c>unalias</c> — removes aliases.</summary>
public sealed class UnaliasBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "unalias";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (context.Arguments.Contains("-a"))
        {
            context.State.Aliases.Clear();
            return ValueTask.FromResult(ExecResult.Success);
        }

        var missing = false;
        foreach (var argument in context.Arguments)
        {
            if (!context.State.Aliases.Remove(argument))
            {
                missing = true;
            }
        }

        return ValueTask.FromResult(missing
            ? ExecResult.Error($"unalias: not found\n", ExitCodes.Failure)
            : ExecResult.Success);
    }
}

/// <summary><c>type</c> — reports how a name would be interpreted.</summary>
public sealed class TypeBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "type";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var typeOnly = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-t": typeOnly = true; break;
                case "-a" or "-f" or "-P" or "-p": break;
                default:
                    return ValueTask.FromResult(ExecResult.Usage("type", $"invalid option -- '{option.TrimStart('-')}'"));
            }
        }

        var builder = new StringBuilder();
        var allFound = true;

        foreach (var name in cursor.Operands)
        {
            var kind = Classify(context, name);

            if (kind is null)
            {
                allFound = false;
                continue;
            }

            builder.Append(typeOnly ? kind + "\n" : Describe(name, kind));
        }

        if (!allFound)
        {
            foreach (var name in cursor.Operands.Where(operand => Classify(context, operand) is null))
            {
                builder.Append(typeOnly ? string.Empty : $"bash: type: {name}: not found\n");
            }
        }

        return ValueTask.FromResult(new ExecResult
        {
            Stdout = StreamData.FromText(builder.ToString()),
            ExitCode = allFound ? 0 : 1,
        });
    }

    /// <summary>The words the parser treats as syntax rather than as command names.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "then", "elif", "else", "fi", "case", "esac", "for", "select", "while",
        "until", "do", "done", "in", "function", "time", "{", "}", "!", "[[", "]]", "coproc",
    };

    private static string? Classify(BuiltinContext context, string name)
    {
        if (context.State.Aliases.ContainsKey(name))
        {
            return "alias";
        }

        // A keyword outranks everything: `type if` reports syntax, not a command.
        if (Keywords.Contains(name))
        {
            return "keyword";
        }

        if (context.State.Functions.ContainsKey(name))
        {
            return "function";
        }

        if (context.Hooks.IsBuiltin(name))
        {
            return "builtin";
        }

        return null;
    }

    private static string Describe(string name, string kind) => kind switch
    {
        "alias" => $"{name} is aliased\n",
        "keyword" => $"{name} is a shell keyword\n",
        "function" => $"{name} is a function\n",
        _ => $"{name} is a shell builtin\n",
    };
}
