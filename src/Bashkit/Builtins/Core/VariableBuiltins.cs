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

            var variable = context.State.SetLocal(name, value);
            variable.Attributes |= attributes;
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

                attributes |= ParseAttributes(argument);
                continue;
            }

            sawName = true;
            var equals = argument.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? argument : argument[..equals];
            var value = equals < 0 ? null : argument[(equals + 1)..];

            var variable = local ? context.State.SetLocal(name, null) : context.State.GetOrCreate(name);

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

            // An array literal on the right-hand side is `declare -a x=(1 2 3)`.
            if (value.StartsWith('(') && value.EndsWith(')'))
            {
                variable.SetArray(SplitArrayLiteral(value[1..^1]));
                continue;
            }

            variable.SetScalar(value);
        }

        if (!sawName)
        {
            foreach (var (name, variable) in context.State.AllVariables().OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                printed.Append(Describe(name, variable));
            }

            return ValueTask.FromResult(ExecResult.Ok(printed.ToString()));
        }

        return ValueTask.FromResult(ExecResult.Success);
    }

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

    private static List<string> SplitArrayLiteral(string body) =>
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

            if (argument[1] == 'o')
            {
                index++;
                if (index >= context.Arguments.Count)
                {
                    return ValueTask.FromResult(ExecResult.Ok(DescribeOptions(options)));
                }

                if (!options.SetByName(context.Arguments[index], enable))
                {
                    return ValueTask.FromResult(
                        ExecResult.Usage("set", $"{context.Arguments[index]}: invalid option name"));
                }

                index++;
                continue;
            }

            foreach (var flag in argument[1..])
            {
                var longName = ShellOptions.LongNameForFlag(flag);
                if (longName is null)
                {
                    return ValueTask.FromResult(ExecResult.Usage("set", $"-{flag}: invalid option"));
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

    private static string DescribeOptions(ShellOptions options)
    {
        var names = new[]
        {
            "allexport", "errexit", "noclobber", "noexec", "noglob", "nounset", "pipefail", "verbose", "xtrace",
        };

        var builder = new StringBuilder();
        foreach (var name in names)
        {
            builder.Append(name.PadRight(16)).Append(options.GetByName(name) == true ? "on" : "off").Append('\n');
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
        var names = new List<string>();

        foreach (var argument in context.Arguments)
        {
            switch (argument)
            {
                case "-s": enable = true; continue;
                case "-u": disable = true; continue;
                case "-q": query = true; continue;
                case "-o" or "-p": continue;
                default:
                    names.Add(argument);
                    continue;
            }
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

                builder.Append(name.PadRight(20)).Append(value ? "on" : "off").Append('\n');
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
                    return ValueTask.FromResult(ExecResult.Usage("shopt", $"{name}: invalid shell option name"));
                }

                continue;
            }

            var value = options.GetByName(name);
            if (value is null)
            {
                return ValueTask.FromResult(ExecResult.Usage("shopt", $"{name}: invalid shell option name"));
            }

            allSet &= value.Value;

            if (!query)
            {
                output.Append(name.PadRight(20)).Append(value.Value ? "on" : "off").Append('\n');
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
