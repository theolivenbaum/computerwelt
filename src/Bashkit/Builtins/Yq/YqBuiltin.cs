using System.Globalization;
using System.Text;
using Bashkit.Builtins.Jq;
using Bashkit.Builtins.Yq;

namespace Bashkit.Builtins;

/// <summary>
/// <c>yq</c> — the jq language over YAML.
/// </summary>
/// <remarks>
/// <para>
/// yq is a front end, not a second engine: a YAML document is read into the same value
/// model jq uses, the filter runs through the same evaluator, and the result is written
/// back as YAML or JSON. Every jq filter therefore works here unchanged, which is the whole
/// reason yq is worth having.
/// </para>
/// <para>
/// Input and output formats are independent, so <c>yq -p=json -o=yaml</c> converts one way
/// and <c>yq -o=json</c> the other.
/// </para>
/// </remarks>
public sealed class YqBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "yq";

    /// <inheritdoc />
    public string? LlmHint =>
        "yq: YAML processor using the jq filter language. Reads and writes YAML or JSON "
        + "(-p/-o), supports -r, -e, -n, -s, -N, -I/--indent, --expression.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: yq [eval] [OPTIONS] [FILTER] [FILES...]

          -p, --input-format FMT    read yaml (default) or json
          -o, --output-format FMT   write yaml (default) or json
          -I, --indent N            indentation width; 0 means compact JSON
          -r, --raw-output          output strings without quoting
          -e, --exit-status         set the exit status from the last output
          -n, --null-input          use null as the single input
          -s, --slurp               read all documents into one array
          -N, --no-doc-separators   omit the `---` between output documents
              --expression EXPR     supply the filter as an option
              --help                display this help and exit
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(
        BuiltinContext context,
        CancellationToken cancellationToken = default)
    {
        var options = ParseArguments(context.Arguments);

        if (options.Result is { } early)
        {
            return early;
        }

        JqNode filter;

        try
        {
            filter = JqPrelude.Wrap(JqParser.Parse(options.Expression));
        }
        catch (JqException exception)
        {
            return ExecResult.Error($"yq: {exception.Message}\n", 1);
        }

        var inputs = new List<JsonValue>();

        foreach (var source in options.Files.Count > 0 ? options.Files : ["-"])
        {
            var text = source == "-"
                ? context.StdinText
                : await ReadTextAsync(context, source, cancellationToken);

            if (text is null)
            {
                return ExecResult.Error($"yq: open {source}: no such file or directory\n", 1);
            }

            context.Budget.ChargeWork(text.Length);
            var json = options.JsonInput ?? source.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

            try
            {
                inputs.AddRange(json ? JsonReader.ParseAll(text) : YamlReader.ParseAll(text));
            }
            catch (YamlException exception)
            {
                return ExecResult.Error($"yq: invalid YAML: {exception.Message}\n", 1);
            }
            catch (JqException exception)
            {
                return ExecResult.Error($"yq: invalid JSON: {exception.Message}\n", 1);
            }
        }

        // An empty stream is one null document, which is what yq prints for empty input.
        if (inputs.Count == 0)
        {
            inputs.Add(JsonNull.Instance);
        }

        if (options.Slurp)
        {
            inputs = [new JsonArray(inputs)];
        }

        if (options.NullInput)
        {
            inputs = [JsonNull.Instance];
        }

        return Run(context, options, filter, inputs);
    }

    private static ExecResult Run(BuiltinContext context, YqOptions options, JqNode filter, List<JsonValue> inputs)
    {
        var stdout = new StringBuilder();
        using IEnumerator<JsonValue> cursor = inputs.GetEnumerator();

        var runtime = new JqRuntime(BuildEnvironment(context), context.Budget)
        {
            Inputs = cursor,
            Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        var evaluator = new JqEvaluator(runtime);
        var outputs = new List<JsonValue>();

        while (cursor.MoveNext())
        {
            runtime.InputNumber++;

            try
            {
                outputs.AddRange(evaluator.Eval(filter, cursor.Current, JqScope.Empty));
            }
            catch (JqException exception)
            {
                return ExecResult.Error($"yq: error: {exception.Message}\n", 1);
            }
        }

        for (var i = 0; i < outputs.Count; i++)
        {
            // `---` separates documents in YAML output only; a JSON stream is just values.
            if (i > 0 && !options.JsonOutput && !options.NoSeparators)
            {
                stdout.Append("---\n");
            }

            Emit(stdout, outputs[i], options);
        }

        var exitCode = options.ExitStatus && (outputs.Count == 0 || !outputs[^1].IsTruthy) ? 1 : 0;

        return new ExecResult
        {
            Stdout = StreamData.FromText(stdout.ToString()),
            Stderr = StreamData.FromText(runtime.Stderr.ToString()),
            ExitCode = exitCode,
        };
    }

    private static void Emit(StringBuilder stdout, JsonValue value, YqOptions options)
    {
        if (options.Raw && value is JsonString text)
        {
            stdout.Append(text.Value).Append('\n');
            return;
        }

        if (!options.JsonOutput)
        {
            stdout.Append(YamlWriter.Write(value));
            return;
        }

        var format = options.Indent <= 0
            ? JsonFormat.Compact with { SortKeys = true }
            : new JsonFormat(new string(' ', options.Indent), SortKeys: true);

        stdout.Append(JsonWriter.Write(value, format)).Append('\n');
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

    private static async ValueTask<string?> ReadTextAsync(
        BuiltinContext context,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return Encoding.UTF8.GetString(
                await context.FileSystem.ReadFileAsync(context.ResolvePath(path), cancellationToken));
        }
        catch (BashkitException)
        {
            return null;
        }
    }

    private sealed class YqOptions
    {
        public string Expression { get; set; } = ".";

        public List<string> Files { get; } = [];

        public bool? JsonInput { get; set; }

        public bool JsonOutput { get; set; }

        public int Indent { get; set; } = 2;

        public bool Raw { get; set; }

        public bool ExitStatus { get; set; }

        public bool NullInput { get; set; }

        public bool Slurp { get; set; }

        public bool NoSeparators { get; set; }

        public bool ExpressionGiven { get; set; }

        public ExecResult? Result { get; set; }
    }

    private YqOptions ParseArguments(IReadOnlyList<string> arguments)
    {
        var options = new YqOptions();
        var operands = new List<string>();

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];

            // `yq eval FILTER` and its `e` abbreviation name the default subcommand.
            if (operands.Count == 0 && !options.ExpressionGiven && argument is "eval" or "e")
            {
                continue;
            }

            if (argument == "-" || !argument.StartsWith('-'))
            {
                operands.Add(argument);
                continue;
            }

            var (name, attached) = SplitOption(argument);

            switch (name)
            {
                case "--help" or "-h":
                    options.Result = ExecResult.Ok(Help + "\n");
                    return options;

                case "--input-format" or "-p":
                    options.JsonInput = Take(arguments, ref i, attached)
                        ?.StartsWith('j') == true;
                    continue;

                case "--output-format" or "-o":
                    options.JsonOutput = Take(arguments, ref i, attached)
                        ?.StartsWith('j') == true;
                    continue;

                case "--indent" or "-I":
                    options.Indent = int.TryParse(
                        Take(arguments, ref i, attached),
                        CultureInfo.InvariantCulture,
                        out var width)
                        ? width
                        : 2;
                    continue;

                case "--expression":
                    options.Expression = Take(arguments, ref i, attached) ?? ".";
                    options.ExpressionGiven = true;
                    continue;

                case "--raw-output":
                    options.Raw = true;
                    continue;

                case "--join-output":
                    options.Raw = true;
                    continue;

                case "--exit-status":
                    options.ExitStatus = true;
                    continue;

                case "--null-input":
                    options.NullInput = true;
                    continue;

                case "--slurp":
                    options.Slurp = true;
                    continue;

                case "--no-doc-separators":
                    options.NoSeparators = true;
                    continue;
            }

            if (name.StartsWith("--", StringComparison.Ordinal))
            {
                options.Result = ExecResult.Usage(Name, $"unknown flag: {name}", 1);
                return options;
            }

            if (!ApplyShortFlags(options, name))
            {
                options.Result = ExecResult.Usage(Name, $"unknown shorthand flag in: {argument}", 1);
                return options;
            }
        }

        AssignOperands(options, operands);
        return options;
    }

    /// <summary>Splits <c>-o=json</c> and <c>--indent=4</c> into their name and value.</summary>
    private static (string Name, string? Value) SplitOption(string argument)
    {
        var equals = argument.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? (argument, null) : (argument[..equals], argument[(equals + 1)..]);
    }

    private static string? Take(IReadOnlyList<string> arguments, ref int index, string? attached)
    {
        if (attached is not null)
        {
            return attached;
        }

        return index + 1 < arguments.Count ? arguments[++index] : null;
    }

    private static bool ApplyShortFlags(YqOptions options, string bundle)
    {
        foreach (var flag in bundle[1..])
        {
            switch (flag)
            {
                case 'r': options.Raw = true; break;
                case 'j': options.Raw = true; break;
                case 'e': options.ExitStatus = true; break;
                case 'n': options.NullInput = true; break;
                case 's': options.Slurp = true; break;
                case 'N': options.NoSeparators = true; break;
                case 'M' or 'C' or 'P': break;
                default: return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Decides which operand is the filter.
    /// </summary>
    /// <remarks>
    /// yq allows both <c>yq '.a' file.yml</c> and <c>yq file.yml</c>, and nothing in the
    /// syntax separates them — so an operand that looks like a path is taken as a file and
    /// the filter defaults to <c>.</c>, exactly as yq itself decides.
    /// </remarks>
    private static void AssignOperands(YqOptions options, List<string> operands)
    {
        if (operands.Count == 0)
        {
            return;
        }

        var start = 0;

        if (!options.ExpressionGiven && !LooksLikeFile(operands[0]))
        {
            options.Expression = operands[0];
            start = 1;
        }

        for (var i = start; i < operands.Count; i++)
        {
            options.Files.Add(operands[i]);
        }
    }

    private static bool LooksLikeFile(string operand) =>
        operand == "-"
        || operand.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
        || operand.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
        || operand.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        || (operand.StartsWith('/') && !operand.Contains(' ', StringComparison.Ordinal));
}
