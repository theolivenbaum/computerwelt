using System.Globalization;
using System.Text;
using Bashkit.Interpreter;

namespace Bashkit.Builtins;

/// <summary><c>export</c> — marks variables for the environment of commands.</summary>
public sealed class ExportBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "export";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (context.Arguments.Count == 0)
        {
            var builder = new StringBuilder();
            foreach (var (name, variable) in context.State.AllVariables().OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                if (variable.IsExported)
                {
                    builder.Append("declare -x ").Append(name).Append('=')
                        .Append(Quote(variable.Value)).Append('\n');
                }
            }

            return ValueTask.FromResult(ExecResult.Ok(builder.ToString()));
        }

        var remove = false;

        foreach (var argument in context.Arguments)
        {
            if (argument == "-n")
            {
                remove = true;
                continue;
            }

            if (argument is "-p" or "-f")
            {
                continue;
            }

            var equals = argument.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? argument : argument[..equals];

            if (!IsValidName(name))
            {
                return ValueTask.FromResult(ExecResult.Usage("export", $"`{argument}': not a valid identifier"));
            }

            var variable = context.State.GetOrCreate(name);

            if (equals >= 0)
            {
                variable.SetScalar(argument[(equals + 1)..]);
            }

            if (remove)
            {
                variable.Attributes &= ~VariableAttributes.Exported;
            }
            else
            {
                variable.Attributes |= VariableAttributes.Exported;

                // Exporting explicitly makes the name part of the script's own
                // environment, even when the shell had seeded it.
                context.State.ShellDefaults.Remove(name);
            }
        }

        return ValueTask.FromResult(ExecResult.Success);
    }

    internal static bool IsValidName(string name) =>
        name.Length > 0
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_');

    internal static string Quote(string value) => "\"" + value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("$", "\\$", StringComparison.Ordinal) + "\"";
}

/// <summary><c>unset</c> — removes variables and functions.</summary>
public sealed class UnsetBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "unset";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var functionsOnly = false;
        var variablesOnly = false;

        foreach (var argument in context.Arguments)
        {
            switch (argument)
            {
                case "-f":
                    functionsOnly = true;
                    continue;
                case "-v":
                    variablesOnly = true;
                    continue;
            }

            // `unset a[1]` removes a single element rather than the whole array.
            var bracket = argument.IndexOf('[', StringComparison.Ordinal);
            if (bracket > 0 && argument.EndsWith(']'))
            {
                var name = argument[..bracket];
                var subscript = argument[(bracket + 1)..^1];
                var variable = context.State.Lookup(name);

                if (variable is not null)
                {
                    var key = variable.IsAssociative
                        ? subscript
                        : ArithmeticEvaluator.Evaluate(context.State, subscript).ToString(CultureInfo.InvariantCulture);
                    variable.UnsetElement(key);
                }

                continue;
            }

            if (!functionsOnly)
            {
                context.State.Unset(argument);
            }

            if (!variablesOnly)
            {
                context.State.Functions.Remove(argument);
            }
        }

        return ValueTask.FromResult(ExecResult.Success);
    }
}

/// <summary><c>readonly</c> — marks variables as unassignable.</summary>
public sealed class ReadOnlyBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "readonly";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        foreach (var argument in context.Arguments)
        {
            if (argument.StartsWith('-'))
            {
                continue;
            }

            var equals = argument.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? argument : argument[..equals];
            var variable = context.State.GetOrCreate(name);

            if (equals >= 0)
            {
                variable.SetScalar(argument[(equals + 1)..]);
            }

            variable.Attributes |= VariableAttributes.ReadOnly;
        }

        return ValueTask.FromResult(ExecResult.Success);
    }
}

/// <summary><c>local</c> — declares variables in the current function scope.</summary>
public sealed class LocalBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "local";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (context.State.ScopeDepth == 0)
        {
            return ValueTask.FromResult(ExecResult.Usage("local", "can only be used in a function", ExitCodes.Failure));
        }

        var attributes = VariableAttributes.None;

        foreach (var argument in context.Arguments)
        {
            if (argument.StartsWith('-') && argument.Length > 1)
            {
                attributes |= DeclareBuiltin.ParseAttributes(argument);
                continue;
            }

            var equals = argument.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? argument : argument[..equals];
            var value = equals < 0 ? null : argument[(equals + 1)..];

            var variable = context.State.SetLocal(name, null);
            variable.Attributes |= attributes;

            if (value is null)
            {
                continue;
            }

            // `local arr=(a b c)` declares a local array; the parenthesised form reaches
            // here as one already-expanded word.
            if (value.StartsWith('(') && value.EndsWith(')'))
            {
                DeclareBuiltin.AssignArrayLiteral(context, variable, DeclareBuiltin.SplitArrayLiteral(value[1..^1]));
                continue;
            }

            DeclareBuiltin.AssignScalar(context, variable, value);
        }

        return ValueTask.FromResult(ExecResult.Success);
    }
}

/// <summary><c>declare</c> / <c>typeset</c> — declares variables with attributes.</summary>
public sealed class DeclareBuiltin : IBuiltin
{
    /// <summary>Creates the builtin under <paramref name="name"/>, which may be <c>typeset</c>.</summary>
    public DeclareBuiltin(string name = "declare") => Name = name;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var attributes = VariableAttributes.None;
        var remove = false;
        var local = context.State.ScopeDepth > 0;
        var printed = new StringBuilder();
        var sawName = false;
        var print = false;
        var functions = false;
        var namesOnly = false;
        var missing = false;

        foreach (var argument in context.Arguments)
        {
            if (argument.Length > 1 && argument[0] is '-' or '+')
            {
                if (argument[0] == '+')
                {
                    remove = true;
                }

                if (argument.Contains('g', StringComparison.Ordinal))
                {
                    local = false;
                }

                if (argument.Contains('p', StringComparison.Ordinal))
                {
                    print = true;
                }

                if (argument.Contains('f', StringComparison.Ordinal))
                {
                    functions = true;
                }

                if (argument.Contains('F', StringComparison.Ordinal))
                {
                    functions = true;
                    namesOnly = true;
                }

                attributes |= ParseAttributes(argument);
                continue;
            }

            sawName = true;

            // `-p name` reports the declaration rather than making one.
            if (print || functions)
            {
                if (functions)
                {
                    if (context.State.Functions.TryGetValue(argument, out var function))
                    {
                        printed.Append(namesOnly ? $"declare -f {argument}\n" : Definition(argument, function));
                    }
                    else
                    {
                        missing = true;
                    }

                    continue;
                }

                if (context.State.LookupRaw(argument) is { } declared)
                {
                    printed.Append(Describe(argument, declared));
                }
                else
                {
                    missing = true;
                }

                continue;
            }

            var equals = argument.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? argument : argument[..equals];
            var value = equals < 0 ? null : argument[(equals + 1)..];

            // Attribute changes apply to the named variable itself, never to whatever a
            // nameref of that name points at.
            var variable = context.State.LookupRaw(name)
                ?? (local ? context.State.SetLocal(name, null) : context.State.GetOrCreate(name));

            if (remove)
            {
                variable.Attributes &= ~attributes;
            }
            else
            {
                variable.Attributes |= attributes;
            }

            if (value is null)
            {
                continue;
            }

            // An array literal on the right-hand side is `declare -a x=(1 2 3)`, or with
            // explicit subscripts `declare -A x=([k]=v ...)`.
            if (value.StartsWith('(') && value.EndsWith(')'))
            {
                AssignArrayLiteral(context, variable, SplitArrayLiteral(value[1..^1]));
                continue;
            }

            AssignScalar(context, variable, value);
        }

        if (!sawName)
        {
            if (functions)
            {
                foreach (var (name, function) in context.State.Functions.OrderBy(static p => p.Key, StringComparer.Ordinal))
                {
                    printed.Append(namesOnly ? $"declare -f {name}\n" : Definition(name, function));
                }
            }
            else
            {
                foreach (var (name, variable) in context.State.AllVariables().OrderBy(static p => p.Key, StringComparer.Ordinal))
                {
                    printed.Append(Describe(name, variable));
                }
            }

            return ValueTask.FromResult(ExecResult.Ok(printed.ToString()));
        }

        if (printed.Length > 0 || missing)
        {
            return ValueTask.FromResult(new ExecResult
            {
                Stdout = StreamData.FromText(printed.ToString()),
                ExitCode = missing ? ExitCodes.Failure : 0,
            });
        }

        return ValueTask.FromResult(ExecResult.Success);
    }

    /// <summary>
    /// Assigns a scalar, applying whatever attributes the variable carries.
    /// </summary>
    /// <remarks>
    /// <c>declare -i x=5+3</c> stores 8, not the text: the integer attribute makes every
    /// later assignment an arithmetic evaluation, and <c>-u</c>/<c>-l</c> fold case the
    /// same way.
    /// </remarks>
    internal static void AssignScalar(BuiltinContext context, ShellVariable variable, string value)
    {
        if (variable.Attributes.HasFlag(VariableAttributes.Integer))
        {
            var numeric = Interpreter.ArithmeticEvaluator.Evaluate(context.State, value);
            variable.SetScalar(numeric.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (variable.Attributes.HasFlag(VariableAttributes.UpperCase))
        {
            variable.SetScalar(value.ToUpperInvariant());
            return;
        }

        if (variable.Attributes.HasFlag(VariableAttributes.LowerCase))
        {
            variable.SetScalar(value.ToLowerInvariant());
            return;
        }

        variable.SetScalar(value);
    }

    /// <summary>Renders a function definition, preferring its own source text.</summary>
    private static string Definition(string name, Parsing.FunctionDef function) =>
        function.Source is { Length: > 0 } source ? source + "\n" : $"{name} ()\n{{\n}}\n";

    internal static VariableAttributes ParseAttributes(string flags)
    {
        var attributes = VariableAttributes.None;

        foreach (var flag in flags[1..])
        {
            attributes |= flag switch
            {
                'x' => VariableAttributes.Exported,
                'r' => VariableAttributes.ReadOnly,
                'i' => VariableAttributes.Integer,
                'a' => VariableAttributes.IndexedArray,
                'A' => VariableAttributes.AssociativeArray,
                'n' => VariableAttributes.NameRef,
                'u' => VariableAttributes.UpperCase,
                'l' => VariableAttributes.LowerCase,
                _ => VariableAttributes.None,
            };
        }

        return attributes;
    }

    private static string Describe(string name, ShellVariable variable)
    {
        var flags = new StringBuilder("-");

        if (variable.IsAssociative)
        {
            flags.Append('A');
        }
        else if (variable.IsArray)
        {
            flags.Append('a');
        }

        if (variable.Attributes.HasFlag(VariableAttributes.Integer))
        {
            flags.Append('i');
        }

        if (variable.IsReadOnly)
        {
            flags.Append('r');
        }

        if (variable.IsExported)
        {
            flags.Append('x');
        }

        if (flags.Length == 1)
        {
            flags.Append('-');
        }

        if (variable.IsArray)
        {
            var elements = string.Join(' ', variable.Keys.Zip(
                variable.Elements,
                static (key, value) => $"[{key}]=\"{value}\""));
            return $"declare {flags} {name}=({elements})\n";
        }

        return $"declare {flags} {name}=\"{variable.Value}\"\n";
    }

    /// <summary>
    /// Assigns the elements of an array literal, honouring explicit <c>[key]=value</c>
    /// subscripts.
    /// </summary>
    /// <remarks>
    /// An associative array can only be filled this way — <c>declare -A x=([a]=1)</c> has
    /// no positional reading — so treating every element as positional would silently turn
    /// the keys into the values.
    /// </remarks>
    internal static void AssignArrayLiteral(BuiltinContext context, ShellVariable variable, List<string> items)
    {
        var positional = new List<string>();

        foreach (var item in items)
        {
            if (!item.StartsWith('[') || item.IndexOf("]=", StringComparison.Ordinal) is not (> 0 and var close))
            {
                positional.Add(item);
                continue;
            }

            var key = item[1..close];
            var element = item[(close + 2)..].Trim('"', '\'');

            if (variable.IsAssociative)
            {
                variable.SetAssociative(key.Trim('"', '\''), element);
                continue;
            }

            variable.SetIndexed(ArithmeticEvaluator.Evaluate(context.State, key), element);
        }

        if (positional.Count > 0 || items.Count == 0)
        {
            variable.SetArray(positional);
        }
    }

    internal static List<string> SplitArrayLiteral(string body) =>
        [.. body.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static piece => piece.Trim('"', '\''))];
}

/// <summary><c>shift</c> — drops leading positional parameters.</summary>
public sealed class ShiftBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "shift";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var count = 1;
        if (context.Arguments.Count > 0
            && !int.TryParse(context.Arguments[0], CultureInfo.InvariantCulture, out count))
        {
            return ValueTask.FromResult(ExecResult.Usage("shift", $"{context.Arguments[0]}: numeric argument required"));
        }

        // Shifting past the end is a failure, and leaves the parameters untouched.
        if (count < 0 || count > context.State.Positional.Count)
        {
            return ValueTask.FromResult(ExecResult.FromExitCode(1));
        }

        context.State.Positional = [.. context.State.Positional.Skip(count)];
        return ValueTask.FromResult(ExecResult.Success);
    }
}

/// <summary><c>set</c> — sets shell options and positional parameters.</summary>
public sealed class SetBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "set";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (context.Arguments.Count == 0)
        {
            var builder = new StringBuilder();
            foreach (var (name, variable) in context.State.AllVariables().OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                builder.Append(name).Append('=').Append(variable.Value).Append('\n');
            }

            return ValueTask.FromResult(ExecResult.Ok(builder.ToString()));
        }

        var index = 0;
        var options = context.State.Options;

        while (index < context.Arguments.Count)
        {
            var argument = context.Arguments[index];

            if (argument == "--")
            {
                index++;
                break;
            }

            if (argument.Length < 2 || (argument[0] != '-' && argument[0] != '+'))
            {
                break;
            }

            var enable = argument[0] == '-';

            var bundle = argument[1..];

            for (var c = 0; c < bundle.Length; c++)
            {
                // `-o name` takes the rest of the bundle's argument or the next one, which
                // is what makes `set -euo pipefail` work.
                if (bundle[c] == 'o')
                {
                    var name = c + 1 < bundle.Length ? bundle[(c + 1)..]
                        : index + 1 < context.Arguments.Count ? context.Arguments[++index] : null;

                    if (name is null)
                    {
                        return ValueTask.FromResult(ExecResult.Ok(DescribeOptions(options, enable)));
                    }

                    if (!options.SetByName(name, enable))
                    {
                        return ValueTask.FromResult(ExecResult.Usage("set", $"{name}: invalid option name"));
                    }

                    c = bundle.Length;
                    continue;
                }

                var longName = ShellOptions.LongNameForFlag(bundle[c]);

                if (longName is null)
                {
                    return ValueTask.FromResult(ExecResult.Usage("set", $"-{bundle[c]}: invalid option"));
                }

                options.SetByName(longName, enable);
            }

            index++;
        }

        // Any remaining words replace the positional parameters.
        if (index < context.Arguments.Count || context.Arguments.Contains("--"))
        {
            context.State.Positional = [.. context.Arguments.Skip(index)];
        }

        return ValueTask.FromResult(ExecResult.Success);
    }

    /// <summary>
    /// Lists the options, in the two shapes bash uses.
    /// </summary>
    /// <remarks>
    /// <c>set -o</c> prints a readable table; <c>set +o</c> prints commands that restore
    /// the current settings, which is the whole point of the form — <c>eval "$(set +o)"</c>
    /// puts the shell back the way it was.
    /// </remarks>
    private static string DescribeOptions(ShellOptions options, bool readable)
    {
        var names = new[]
        {
            "allexport", "errexit", "noclobber", "noexec", "noglob", "nounset", "pipefail", "verbose", "xtrace",
        };

        var builder = new StringBuilder();

        foreach (var name in names)
        {
            var on = options.GetByName(name) == true;

            builder.Append(readable
                ? name.PadRight(15) + "\t" + (on ? "on" : "off")
                : "set " + (on ? "-o " : "+o ") + name)
                .Append('\n');
        }

        return builder.ToString();
    }
}

/// <summary><c>shopt</c> — sets the extended shell options.</summary>
public sealed class ShoptBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "shopt";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var enable = false;
        var disable = false;
        var query = false;
        var print = false;
        var names = new List<string>();

        foreach (var argument in context.Arguments)
        {
            switch (argument)
            {
                case "-s": enable = true; continue;
                case "-u": disable = true; continue;
                case "-q": query = true; continue;
                case "-p": print = true; continue;
                case "-o": continue;
                default:
                    names.Add(argument);
                    continue;
            }
        }

        // `-p` reports settings as the commands that would restore them.
        if (print)
        {
            var restore = new StringBuilder();

            foreach (var name in names.Count > 0 ? names : KnownOptions.ToList())
            {
                if (context.State.Options.GetByName(name) is not { } value)
                {
                    return ValueTask.FromResult(
                        ExecResult.Error($"shopt: {name}: invalid shell option name\n", ExitCodes.Failure));
                }

                restore.Append("shopt ").Append(value ? "-s " : "-u ").Append(name).Append('\n');
            }

            return ValueTask.FromResult(ExecResult.Ok(restore.ToString()));
        }

        var options = context.State.Options;

        if (names.Count == 0)
        {
            var builder = new StringBuilder();
            foreach (var name in KnownOptions)
            {
                var value = options.GetByName(name) == true;
                if ((enable && !value) || (disable && value))
                {
                    continue;
                }

                builder.Append(name.PadRight(32)).Append(value ? "on" : "off").Append('\n');
            }

            return ValueTask.FromResult(ExecResult.Ok(builder.ToString()));
        }

        var allSet = true;
        var output = new StringBuilder();

        foreach (var name in names)
        {
            if (enable || disable)
            {
                if (!options.SetByName(name, enable))
                {
                    return ValueTask.FromResult(
                        ExecResult.Error($"shopt: {name}: invalid shell option name\n", ExitCodes.Failure));
                }

                continue;
            }

            var value = options.GetByName(name);

            if (value is null)
            {
                return ValueTask.FromResult(
                    ExecResult.Error($"shopt: {name}: invalid shell option name\n", ExitCodes.Failure));
            }

            allSet &= value.Value;

            if (!query)
            {
                output.Append(name.PadRight(32)).Append(value.Value ? "on" : "off").Append('\n');
            }
        }

        return ValueTask.FromResult(new ExecResult
        {
            Stdout = StreamData.FromText(output.ToString()),
            ExitCode = enable || disable ? 0 : allSet ? 0 : 1,
        });
    }

    private static readonly string[] KnownOptions =
    [
        "dotglob", "expand_aliases", "extglob", "failglob", "globstar",
        "inherit_errexit", "nocaseglob", "nocasematch", "nullglob",
    ];
}
