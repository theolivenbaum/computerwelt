using System.Text;
using Bashkit.Builtins.Jq;

namespace Bashkit.Builtins;

/// <summary>
/// <c>jq</c> — a command-line JSON processor.
/// </summary>
/// <remarks>
/// <para>
/// A real implementation of the jq language rather than a set of canned queries: a JSON
/// reader that preserves key order, a parser for the full filter grammar, and a streaming
/// evaluator where every filter maps one value to a stream of values. That last property is
/// what jq is, and shortcuts around it break as soon as a filter generates.
/// </para>
/// <para>
/// The standard library is split the way jq splits its own — primitives in
/// <see cref="JqBuiltins"/>, everything derivable in <see cref="JqPrelude"/> as jq source —
/// so derived filters keep working inside path expressions such as <c>del(.a | select(.))</c>.
/// </para>
/// </remarks>
public sealed class JqBuiltin : IBuiltin
{
    /// <summary>Creates the builtin under its usual name.</summary>
    public JqBuiltin()
        : this("jq")
    {
    }

    /// <summary>Creates the builtin under <paramref name="name"/>.</summary>
    public JqBuiltin(string name) => Name = name;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string? LlmHint =>
        "jq: JSON processor. Full filter language: paths, pipes, generators, reduce/foreach, "
        + "assignment, regex, string interpolation. Supports -r, -c, -n, -s, -e, -S, -j, -R, --tab, --arg, --argjson.";

    /// <inheritdoc />
    public string? Help =>
        $"""
        Usage: {Name} [OPTIONS] FILTER [FILES...]

          -c              compact output
          -n              use null as the single input
          -r              output strings raw, without JSON quoting
          -j              like -r, with no trailing newline
          -s              read all inputs into one array
          -e              set the exit status from the last output
          -S              sort object keys
          -R              read each line as a string rather than as JSON
          --tab           indent with tabs
          --arg K V       bind $K to the string V
          --argjson K V   bind $K to the JSON value V
          --slurpfile K F bind $K to the array of JSON values in file F
          --rawfile K F   bind $K to the contents of file F
          --args          treat remaining arguments as positional strings
          --help          display this help and exit
          --version       output version information and exit
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(
        BuiltinContext context,
        CancellationToken cancellationToken = default)
    {
        var options = await ParseArgumentsAsync(context, cancellationToken);

        if (options.Result is { } early)
        {
            return early;
        }

        if (options.Filter is null)
        {
            return ExecResult.Usage(Name, "no filter given", 2);
        }

        JqNode filter;

        try
        {
            filter = JqPrelude.Wrap(JqParser.Parse(options.Filter));
        }
        catch (JqException exception)
        {
            return ExecResult.Error($"{exception.Message}\n", 3);
        }

        var inputs = await ReadInputsAsync(context, options, cancellationToken);

        if (inputs.Failure is { } failure)
        {
            return failure;
        }

        return Run(context, options, filter, inputs.Values, inputs.Filename);
    }

    private ExecResult Run(
        BuiltinContext context,
        JqOptions options,
        JqNode filter,
        List<JsonValue> inputs,
        string? filename)
    {
        var stdout = new StringBuilder();
        var format = options.Compact
            ? JsonFormat.Compact with { SortKeys = options.SortKeys }
            : new JsonFormat(options.Indent, options.SortKeys);

        // Without `-n` the main loop and `input`/`inputs` share one cursor, so `[., input]`
        // reads the record after the current one. With `-n` the loop runs once on null and
        // the whole real stream stays available to `input`.
        // Declared as the interface: List's enumerator is a struct, and letting it box on
        // assignment would hand the runtime an independent copy of the cursor.
        using IEnumerator<JsonValue> cursor = inputs.GetEnumerator();

        var runtime = new JqRuntime(BuildEnvironment(context), context.Budget)
        {
            Inputs = cursor,
            Filename = filename,
            Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        var evaluator = new JqEvaluator(runtime);
        var scope = BuildScope(options);
        var lastWasFalsey = true;
        var produced = false;
        var first = true;

        while (true)
        {
            JsonValue current;

            if (options.NullInput)
            {
                if (!first)
                {
                    break;
                }

                current = JsonNull.Instance;
            }
            else
            {
                if (!cursor.MoveNext())
                {
                    break;
                }

                current = cursor.Current;
                runtime.InputNumber++;
            }

            first = false;

            try
            {
                foreach (var value in evaluator.Eval(filter, current, scope))
                {
                    produced = true;
                    lastWasFalsey = !value.IsTruthy;
                    Emit(stdout, value, options, format);
                }
            }
            catch (JqHaltException halt)
            {
                if (halt.Payload is { } payload)
                {
                    runtime.Stderr.Append(JqValues.ToText(payload));
                }

                return new ExecResult
                {
                    Stdout = StreamData.FromText(stdout.ToString()),
                    Stderr = StreamData.FromText(runtime.Stderr.ToString()),
                    ExitCode = halt.ExitCode,
                };
            }
            catch (JqException exception)
            {
                return new ExecResult
                {
                    Stdout = StreamData.FromText(stdout.ToString()),
                    Stderr = StreamData.FromText($"{runtime.Stderr}jq: error: {exception.Message}\n"),
                    ExitCode = 5,
                };
            }
            catch (JqBreakException)
            {
                // A `break` with no enclosing label ends the filter for this input, which is
                // how jq's own `first/1` terminates its generator.
            }
        }

        // `-e` reports through the exit status: 1 when the last output was null or false,
        // 4 when the filter produced nothing at all.
        var exitCode = options.ExitStatus ? produced ? lastWasFalsey ? 1 : 0 : 4 : 0;

        return new ExecResult
        {
            Stdout = StreamData.FromText(stdout.ToString()),
            Stderr = StreamData.FromText(runtime.Stderr.ToString()),
            ExitCode = exitCode,
        };
    }

    private static void Emit(StringBuilder stdout, JsonValue value, JqOptions options, JsonFormat format)
    {
        if (options.Raw && value is JsonString text)
        {
            stdout.Append(text.Value);
        }
        else
        {
            stdout.Append(JsonWriter.Write(value, format));
        }

        if (!options.NoNewline)
        {
            stdout.Append('\n');
        }
    }

    private static JsonObject BuildEnvironment(BuiltinContext context)
    {
        var environment = new JsonObject();

        foreach (var (name, value) in context.State.ExportedEnvironment())
        {
            environment.Set(name, new JsonString(value));
        }

        return environment;
    }

    private static JqScope BuildScope(JqOptions options)
    {
        var scope = JqScope.Empty;

        foreach (var (name, value) in options.Bindings)
        {
            scope = scope.WithVariable(name, value);
        }

        var positional = new JsonArray(options.Positional.ToList());
        return scope.WithVariable("ARGS", BuildArgs(options, positional));
    }

    private static JsonValue BuildArgs(JqOptions options, JsonArray positional)
    {
        var named = new JsonObject();

        foreach (var (name, value) in options.Bindings)
        {
            named.Set(name, value);
        }

        var args = new JsonObject();
        args.Set("positional", positional);
        args.Set("named", named);
        return args;
    }

    private sealed class JqOptions
    {
        public string? Filter { get; set; }

        public List<string> Files { get; } = [];

        public List<(string Name, JsonValue Value)> Bindings { get; } = [];

        public List<JsonValue> Positional { get; } = [];

        public bool Compact { get; set; }

        public bool Raw { get; set; }

        public bool NoNewline { get; set; }

        public bool NullInput { get; set; }

        public bool Slurp { get; set; }

        public bool RawInput { get; set; }

        public bool SortKeys { get; set; }

        public bool ExitStatus { get; set; }

        public string Indent { get; set; } = "  ";

        public ExecResult? Result { get; set; }
    }

    private async ValueTask<JqOptions> ParseArgumentsAsync(BuiltinContext context, CancellationToken cancellationToken)
    {
        var options = new JqOptions();
        var arguments = context.Arguments;
        var positionalOnly = false;

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];

            if (positionalOnly)
            {
                options.Positional.Add(new JsonString(argument));
                continue;
            }

            switch (argument)
            {
                case "--help" or "-h":
                    options.Result = ExecResult.Ok(Help + "\n");
                    return options;

                case "--version" or "-V":
                    options.Result = ExecResult.Ok("jq-1.8\n");
                    return options;

                case "--compact-output":
                    options.Compact = true;
                    continue;

                case "--raw-output":
                    options.Raw = true;
                    continue;

                case "--join-output":
                    options.Raw = true;
                    options.NoNewline = true;
                    continue;

                case "--null-input":
                    options.NullInput = true;
                    continue;

                case "--slurp":
                    options.Slurp = true;
                    continue;

                case "--raw-input":
                    options.RawInput = true;
                    continue;

                case "--sort-keys":
                    options.SortKeys = true;
                    continue;

                case "--exit-status":
                    options.ExitStatus = true;
                    continue;

                case "--tab":
                    options.Indent = "\t";
                    continue;

                case "--indent" when i + 1 < arguments.Count:
                    options.Indent = new string(' ', Math.Clamp(int.TryParse(arguments[++i], out var width) ? width : 2, 0, 7));
                    continue;

                case "--arg" when i + 2 < arguments.Count:
                    options.Bindings.Add((arguments[i + 1], new JsonString(arguments[i + 2])));
                    i += 2;
                    continue;

                case "--argjson" when i + 2 < arguments.Count:
                {
                    try
                    {
                        options.Bindings.Add((arguments[i + 1], JsonReader.Parse(arguments[i + 2])));
                    }
                    catch (JqException exception)
                    {
                        options.Result = ExecResult.Error($"{Name}: invalid JSON text passed to --argjson: {exception.Message}\n", 2);
                        return options;
                    }

                    i += 2;
                    continue;
                }

                case "--rawfile" when i + 2 < arguments.Count:
                {
                    var text = await ReadTextAsync(context, arguments[i + 2], cancellationToken);

                    if (text is null)
                    {
                        options.Result = ExecResult.Error($"{Name}: cannot open {arguments[i + 2]}\n", 2);
                        return options;
                    }

                    options.Bindings.Add((arguments[i + 1], new JsonString(text)));
                    i += 2;
                    continue;
                }

                case "--slurpfile" when i + 2 < arguments.Count:
                {
                    var text = await ReadTextAsync(context, arguments[i + 2], cancellationToken);

                    if (text is null)
                    {
                        options.Result = ExecResult.Error($"{Name}: cannot open {arguments[i + 2]}\n", 2);
                        return options;
                    }

                    options.Bindings.Add((arguments[i + 1], new JsonArray(JsonReader.ParseAll(text))));
                    i += 2;
                    continue;
                }

                case "--args":
                    positionalOnly = true;
                    continue;

                case "--":
                    positionalOnly = true;
                    continue;
            }

            // Short flags bundle: `-rn` is `-r -n`.
            if (argument.Length > 1 && argument[0] == '-' && argument[1] != '-')
            {
                if (!ApplyShortFlags(options, argument))
                {
                    options.Result = ExecResult.Usage(Name, $"Unknown option: {argument}", 2);
                    return options;
                }

                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                options.Result = ExecResult.Usage(Name, $"Unknown option: {argument}", 2);
                return options;
            }

            if (options.Filter is null)
            {
                options.Filter = argument;
                continue;
            }

            options.Files.Add(argument);
        }

        return options;
    }

    private static bool ApplyShortFlags(JqOptions options, string argument)
    {
        foreach (var flag in argument[1..])
        {
            switch (flag)
            {
                case 'c': options.Compact = true; break;
                case 'r': options.Raw = true; break;
                case 'j': options.Raw = true; options.NoNewline = true; break;
                case 'n': options.NullInput = true; break;
                case 's': options.Slurp = true; break;
                case 'R': options.RawInput = true; break;
                case 'S': options.SortKeys = true; break;
                case 'e': options.ExitStatus = true; break;
                case 'a': break;
                default: return false;
            }
        }

        return true;
    }

    private static async ValueTask<string?> ReadTextAsync(
        BuiltinContext context,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return Encoding.UTF8.GetString(await context.FileSystem.ReadFileAsync(context.ResolvePath(path), cancellationToken));
        }
        catch (BashkitException)
        {
            return null;
        }
    }

    private async ValueTask<(List<JsonValue> Values, string? Filename, ExecResult? Failure)> ReadInputsAsync(
        BuiltinContext context,
        JqOptions options,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        string? filename = null;

        if (options.Files.Count == 0)
        {
            text.Append(context.StdinText);
        }
        else
        {
            foreach (var file in options.Files)
            {
                var content = await ReadTextAsync(context, file, cancellationToken);

                if (content is null)
                {
                    return ([], null, ExecResult.Error($"{Name}: error: Could not open {file}: No such file or directory\n", 2));
                }

                context.Budget.ChargeWork(content.Length);
                text.Append(content);
                filename ??= file;
            }
        }

        List<JsonValue> values;

        try
        {
            values = options.RawInput
                ? ReadRaw(text.ToString(), options.Slurp)
                : JsonReader.ParseAll(text.ToString());
        }
        catch (JqException exception)
        {
            return ([], filename, ExecResult.Error($"{Name}: error (at <stdin>:0): {exception.Message}\n", 2));
        }

        if (options.Slurp && !options.RawInput)
        {
            values = [new JsonArray(values)];
        }

        return (values, filename, null);
    }

    /// <summary>Reads input as text: one string per line, or one string for the whole stream.</summary>
    private static List<JsonValue> ReadRaw(string text, bool slurp)
    {
        if (slurp)
        {
            return [new JsonString(text)];
        }

        if (text.Length == 0)
        {
            return [];
        }

        var trimmed = text.EndsWith('\n') ? text[..^1] : text;
        return [.. trimmed.Split('\n').Select(static line => (JsonValue)new JsonString(line))];
    }
}
