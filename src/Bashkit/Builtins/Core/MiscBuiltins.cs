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
        var environment = context.State.ExportedEnvironment();

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

        var input = context.StdinText;

        if (input.Length == 0)
        {
            return ValueTask.FromResult(ExecResult.FromExitCode(1));
        }

        var newline = input.IndexOf(delimiter, StringComparison.Ordinal);
        var line = newline < 0 ? input : input[..newline];

        if (maxChars is { } limit && line.Length > limit)
        {
            line = line[..limit];
        }

        if (!rawMode)
        {
            line = RemoveBackslashes(line);
        }

        var names = cursor.Operands;

        if (arrayName is not null)
        {
            var variable = context.State.GetOrCreate(arrayName);
            variable.SetArray(SplitFields(line, context.State.Ifs));
            return ValueTask.FromResult(ExecResult.Success);
        }

        if (names.Count == 0)
        {
            context.State.Set("REPLY", line);
            return ValueTask.FromResult(ExecResult.Success);
        }

        var fields = SplitFields(line, context.State.Ifs);

        for (var i = 0; i < names.Count; i++)
        {
            // The last named variable receives every remaining field, joined by a space.
            var value = i == names.Count - 1
                ? string.Join(' ', fields.Skip(i))
                : i < fields.Count ? fields[i] : string.Empty;

            context.State.Set(names[i], value);
        }

        return ValueTask.FromResult(ExecResult.Success);
    }

    private static List<string> SplitFields(string line, string ifs)
    {
        if (ifs.Length == 0)
        {
            return [line];
        }

        return [.. line.Split(ifs.ToCharArray(), StringSplitOptions.RemoveEmptyEntries)];
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

        return ValueTask.FromResult(new ExecResult
        {
            Stdout = StreamData.FromText(builder.ToString()),
            Stderr = allFound ? StreamData.Empty : StreamData.FromText("bash: type: not found\n"),
            ExitCode = allFound ? 0 : 1,
        });
    }

    private static string? Classify(BuiltinContext context, string name)
    {
        if (context.State.Aliases.ContainsKey(name))
        {
            return "alias";
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
        "function" => $"{name} is a function\n",
        _ => $"{name} is a shell builtin\n",
    };
}
