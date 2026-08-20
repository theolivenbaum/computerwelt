using System.Text;
using Computerwelt.Emulation.Bash.Builtins.Awk;

namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// <c>awk</c> — a pattern-scanning and processing language.
/// </summary>
/// <remarks>
/// <para>
/// A complete AWK rather than a handful of special cases: the program is lexed, parsed into
/// an AST and walked. Anything less falls apart on the second real one-liner it meets,
/// because AWK's grammar is genuinely context-sensitive — <c>/</c> is division or a regex
/// depending on what came before, adjacency means concatenation, and <c>&gt;</c> inside
/// <c>print</c> redirects rather than compares.
/// </para>
/// <para>
/// Two AWK features need a process and therefore do not exist here: <c>system()</c> and
/// pipes in either direction. They report failure rather than pretending to work.
/// </para>
/// </remarks>
public sealed class AwkBuiltin : IBuiltin
{
    /// <summary>Creates the builtin under its usual name.</summary>
    public AwkBuiltin()
        : this("awk")
    {
    }

    /// <summary>Creates the builtin under <paramref name="name"/>, for <c>gawk</c> and <c>mawk</c>.</summary>
    public AwkBuiltin(string name) => Name = name;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string? LlmHint =>
        "awk: Pattern scanning and processing. Full language: BEGIN/END, patterns, ranges, "
        + "functions, arrays, getline, printf. Supports -F, -v, -f.";

    /// <inheritdoc />
    public string? Help =>
        $"""
        Usage: {Name} [OPTION]... 'program' [FILE]...
        Pattern scanning and processing language.

          -F SEP        use SEP as the field separator
          -v var=val    assign a variable before the program runs
          -f progfile   read the program from a file
          --help        display this help and exit
          --version     output version information and exit
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

        if (options.Program is null)
        {
            return ExecResult.Usage(Name, "no program given", 2);
        }

        AwkProgram program;

        try
        {
            program = AwkParser.Parse(options.Program);
        }
        catch (BashkitException exception)
        {
            return ExecResult.Error($"{exception.Message}\n", 2);
        }

        var interpreter = new AwkInterpreter(program, context.Budget, name => ReadFile(context, name));

        if (options.FieldSeparator is { } separator)
        {
            interpreter.SetFieldSeparator(separator);
        }

        foreach (var (name, value) in options.Assignments)
        {
            interpreter.Preset(name, value);
        }

        var sources = await ReadSourcesAsync(context, options.Files, cancellationToken);

        if (sources.Failure is { } failure)
        {
            return failure;
        }

        try
        {
            interpreter.Run(sources.Sources);
        }
        catch (AwkRuntimeException exception)
        {
            return ExecResult.Error($"{exception.Message}\n", 2);
        }

        await CommitOutputsAsync(context, interpreter, cancellationToken);

        return new ExecResult
        {
            Stdout = StreamData.FromText(interpreter.Stdout.ToString()),
            Stderr = StreamData.FromText(interpreter.Stderr.ToString()),
            ExitCode = interpreter.ExitCode ?? 0,
        };
    }

    private sealed class AwkOptions
    {
        public string? Program { get; set; }

        public string? FieldSeparator { get; set; }

        public List<(string Name, string Value)> Assignments { get; } = [];

        public List<string> Files { get; } = [];

        public ExecResult? Result { get; set; }
    }

    private async ValueTask<AwkOptions> ParseArgumentsAsync(
        BuiltinContext context,
        CancellationToken cancellationToken)
    {
        var options = new AwkOptions();
        var arguments = context.Arguments;

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];

            switch (argument)
            {
                case "--help":
                    options.Result = ExecResult.Ok(Help + "\n");
                    return options;

                case "--version":
                    options.Result = ExecResult.Ok($"{Name} (bashkit) 0.1\n");
                    return options;

                case "-F" when i + 1 < arguments.Count:
                    options.FieldSeparator = arguments[++i];
                    continue;

                case "-v" when i + 1 < arguments.Count:
                {
                    var assignment = arguments[++i];
                    var equals = assignment.IndexOf('=', StringComparison.Ordinal);

                    if (equals > 0)
                    {
                        options.Assignments.Add((assignment[..equals], assignment[(equals + 1)..]));
                    }

                    continue;
                }

                case "-f" when i + 1 < arguments.Count:
                {
                    var text = ReadFile(context, arguments[++i]);

                    if (text is null)
                    {
                        options.Result = ExecResult.Error($"{Name}: can't open file {arguments[i]}\n", 2);
                        return options;
                    }

                    context.Budget.ChargeWork(text.Length);
                    options.Program = options.Program is null ? text : options.Program + "\n" + text;
                    continue;
                }

                case "--":
                    // Everything after `--` is an operand, program included.
                    for (i++; i < arguments.Count; i++)
                    {
                        AddOperand(options, arguments[i]);
                    }

                    continue;
            }

            if (argument.StartsWith("-F", StringComparison.Ordinal) && argument.Length > 2)
            {
                options.FieldSeparator = argument[2..];
                continue;
            }

            if (argument.StartsWith("-v", StringComparison.Ordinal) && argument.Length > 2)
            {
                var assignment = argument[2..];
                var equals = assignment.IndexOf('=', StringComparison.Ordinal);

                if (equals > 0)
                {
                    options.Assignments.Add((assignment[..equals], assignment[(equals + 1)..]));
                }

                continue;
            }

            // A lone `-` is standard input, not an option.
            if (argument.Length > 1 && argument[0] == '-')
            {
                continue;
            }

            AddOperand(options, argument);
        }

        await ValueTask.CompletedTask;
        return options;
    }

    /// <summary>
    /// Adds an operand: the first is the program unless <c>-f</c> already supplied one, and
    /// the rest are input files.
    /// </summary>
    private static void AddOperand(AwkOptions options, string argument)
    {
        if (options.Program is null)
        {
            options.Program = argument;
            return;
        }

        options.Files.Add(argument);
    }

    /// <summary>
    /// Reads a virtual file synchronously.
    /// </summary>
    /// <remarks>
    /// AWK's <c>getline &lt; "file"</c> can appear anywhere in an expression, and threading
    /// asynchrony through the whole evaluator to serve it would distort every signature for
    /// one rarely-used feature. The virtual filesystems in play complete synchronously, so
    /// the wait is nominal — and a host backing <see cref="IFileSystem"/> with real I/O
    /// pays it only inside <c>awk</c>.
    /// </remarks>
    private static string? ReadFile(BuiltinContext context, string name)
    {
        try
        {
            var read = context.FileSystem.ReadFileAsync(context.ResolvePath(name), context.Budget.CancellationToken);
            var bytes = read.IsCompletedSuccessfully ? read.Result : read.AsTask().GetAwaiter().GetResult();
            return Encoding.UTF8.GetString(bytes);
        }
        catch (BashkitException)
        {
            return null;
        }
    }

    private async ValueTask<(List<AwkInterpreter.AwkSource> Sources, ExecResult? Failure)> ReadSourcesAsync(
        BuiltinContext context,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        var sources = new List<AwkInterpreter.AwkSource>();

        if (files.Count == 0)
        {
            sources.Add(new AwkInterpreter.AwkSource(string.Empty, AwkInterpreter.SplitLines(context.StdinText)));
            return (sources, null);
        }

        foreach (var file in files)
        {
            if (file == "-")
            {
                sources.Add(new AwkInterpreter.AwkSource("-", AwkInterpreter.SplitLines(context.StdinText)));
                continue;
            }

            byte[] bytes;

            try
            {
                bytes = await context.FileSystem.ReadFileAsync(context.ResolvePath(file), cancellationToken);
            }
            catch (BashkitException exception)
            {
                return (sources, ExecResult.Error($"{Name}: {file}: {exception.Message}\n", 2));
            }

            var text = Encoding.UTF8.GetString(bytes);
            context.Budget.ChargeWork(text.Length);
            sources.Add(new AwkInterpreter.AwkSource(file, AwkInterpreter.SplitLines(text)));
        }

        return (sources, null);
    }

    private static async ValueTask CommitOutputsAsync(
        BuiltinContext context,
        AwkInterpreter interpreter,
        CancellationToken cancellationToken)
    {
        foreach (var (name, output) in interpreter.Outputs)
        {
            var bytes = Encoding.UTF8.GetBytes(output.Content.ToString());
            var path = context.ResolvePath(name);

            if (output.Append)
            {
                await context.FileSystem.AppendFileAsync(path, bytes, cancellationToken);
            }
            else
            {
                await context.FileSystem.WriteFileAsync(path, bytes, cancellationToken);
            }
        }
    }
}
