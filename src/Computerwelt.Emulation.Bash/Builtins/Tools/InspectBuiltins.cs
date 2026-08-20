using System.Globalization;
using System.Text;

namespace Computerwelt.Emulation.Bash.Builtins;

/// <summary>
/// <c>file</c> — guess what a file contains.
/// </summary>
/// <remarks>
/// The classification is by content, not by extension: a shebang line names the
/// interpreter, a leading brace or bracket that parses as JSON says so, and anything else
/// is decided by whether the bytes are text. A name-based guess would be wrong exactly when
/// the answer matters.
/// </remarks>
public sealed class FileBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "file";

    /// <inheritdoc />
    public string? LlmHint => "file: Describe a file's type by looking at its contents.";

    /// <inheritdoc />
    public string? Help => "Usage: file [OPTION]... FILE...\nDetermine the type of each FILE.\n";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var operands = new List<string>();
        var brief = false;

        foreach (var argument in context.Arguments)
        {
            switch (argument)
            {
                case "--help":
                    return ExecResult.Ok(Help!);

                case "-b" or "--brief":
                    brief = true;
                    continue;
            }

            if (argument.Length > 1 && argument[0] == '-')
            {
                continue;
            }

            operands.Add(argument);
        }

        if (operands.Count == 0)
        {
            return ExecResult.Usage(Name, "missing operand");
        }

        var output = new StringBuilder();

        foreach (var operand in operands)
        {
            var path = context.ResolvePath(operand);
            string description;

            try
            {
                var metadata = await context.FileSystem.StatAsync(path, cancellationToken);

                description = metadata.IsDirectory ? "directory"
                    : metadata.IsSymlink ? "symbolic link"
                    : metadata.Size == 0 ? "empty"
                    : Classify(await context.FileSystem.ReadFileAsync(path, cancellationToken));
            }
            catch (BashkitException)
            {
                description = "cannot open (No such file or directory)";
            }

            output.Append(brief ? description : $"{operand}: {description}").Append('\n');
        }

        return ExecResult.Ok(output.ToString());
    }

    private static string Classify(byte[] bytes)
    {
        if (bytes.Contains<byte>(0))
        {
            return "data";
        }

        var text = Encoding.UTF8.GetString(bytes);

        if (text.StartsWith("#!", StringComparison.Ordinal))
        {
            var newline = text.IndexOfAny(['\n', '\r']);
            var shebang = newline > 0 ? text[..newline] : text;

            if (shebang.Contains("python", StringComparison.Ordinal))
            {
                return "Python script";
            }

            if (shebang.Contains("bash", StringComparison.Ordinal))
            {
                return "Bourne-Again shell script";
            }

            if (shebang.Contains("/sh", StringComparison.Ordinal))
            {
                return "POSIX shell script";
            }

            return "script text executable";
        }

        var trimmed = text.TrimStart();

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                Jq.JsonReader.Parse(trimmed);
                return "JSON text";
            }
            catch (Jq.JqException)
            {
                // Not JSON after all; fall through to the plain-text answer.
            }
        }

        return text.All(static c => c < 128) ? "ASCII text" : "UTF-8 Unicode text";
    }
}

/// <summary>
/// <c>strings</c> — print the printable runs found in a file.
/// </summary>
/// <remarks>
/// The minimum run length exists to suppress the noise that any binary produces by chance;
/// four characters is the conventional threshold and is what makes the output readable.
/// </remarks>
public sealed class StringsBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "strings";

    /// <inheritdoc />
    public string? LlmHint => "strings: Print printable character runs. Supports -n MIN.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: strings [OPTION]... [FILE]...
        Print the printable character sequences in each FILE.

          -n MIN    print sequences of at least MIN characters (default 4)
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var minimum = 4;
        var operands = new List<string>();

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            var argument = context.Arguments[i];

            if (argument == "--help")
            {
                return ExecResult.Ok(Help + "\n");
            }

            if (argument.StartsWith("-n", StringComparison.Ordinal))
            {
                var text = argument.Length > 2 ? argument[2..]
                    : i + 1 < context.Arguments.Count ? context.Arguments[++i] : null;

                minimum = int.TryParse(text, CultureInfo.InvariantCulture, out var n) ? Math.Max(n, 1) : 4;
                continue;
            }

            if (argument.Length > 1 && argument[0] == '-' && argument != "-")
            {
                continue;
            }

            operands.Add(argument);
        }

        var output = new StringBuilder();

        foreach (var (_, content) in await context.ReadOperandsAsync(operands, cancellationToken))
        {
            context.Budget.ChargeWork(content.Length);
            var run = new StringBuilder();

            foreach (var c in content)
            {
                if (c is >= ' ' and < (char)127 || c == '\t')
                {
                    run.Append(c);
                    continue;
                }

                Flush(output, run, minimum);
            }

            Flush(output, run, minimum);
        }

        return ExecResult.Ok(output.ToString());
    }

    private static void Flush(StringBuilder output, StringBuilder run, int minimum)
    {
        if (run.Length >= minimum)
        {
            output.Append(run).Append('\n');
        }

        run.Clear();
    }
}

/// <summary>
/// <c>column</c> — lay input out in columns.
/// </summary>
/// <remarks>
/// <c>-t</c> is the mode worth having: it aligns the fields of every line into a table,
/// which turns the output of anything that prints records into something readable.
/// </remarks>
public sealed class ColumnBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "column";

    /// <inheritdoc />
    public string? LlmHint => "column: Format input into columns. Supports -t and -s SEPARATOR.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: column [OPTION]... [FILE]...
        Columnate lists.

          -t          create a table, aligning the columns
          -s CHARS    use CHARS as the input field separator
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var table = false;
        string? separator = null;
        var operands = new List<string>();

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            var argument = context.Arguments[i];

            if (argument == "--help")
            {
                return ExecResult.Ok(Help + "\n");
            }

            if (argument.StartsWith("-s", StringComparison.Ordinal))
            {
                separator = argument.Length > 2 ? argument[2..]
                    : i + 1 < context.Arguments.Count ? context.Arguments[++i] : null;

                continue;
            }

            if (argument.Length > 1 && argument[0] == '-' && argument != "-")
            {
                if (argument.Contains('t', StringComparison.Ordinal))
                {
                    table = true;
                }

                continue;
            }

            operands.Add(argument);
        }

        var content = string.Concat((await context.ReadOperandsAsync(operands, cancellationToken))
            .Select(static file => file.Content));

        var lines = content.Length == 0
            ? []
            : (content.EndsWith('\n') ? content[..^1] : content).Split('\n');

        if (lines.Length == 0)
        {
            return ExecResult.Success;
        }

        context.Budget.ChargeWork(lines.Length);
        return ExecResult.Ok(table ? Table(lines, separator) : Fill(lines));
    }

    private static string Table(string[] lines, string? separator)
    {
        var rows = lines
            .Select(line => separator is null
                ? line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                : line.Split(separator.ToCharArray()))
            .ToList();

        var widths = new List<int>();

        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                while (widths.Count <= i)
                {
                    widths.Add(0);
                }

                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        var output = new StringBuilder();

        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                // The last column is not padded, so lines have no trailing blanks.
                output.Append(i == row.Length - 1 ? row[i] : row[i].PadRight(widths[i] + 2));
            }

            output.Append('\n');
        }

        return output.ToString();
    }

    /// <summary>Without <c>-t</c>, column packs the entries across a fixed-width terminal.</summary>
    private static string Fill(string[] lines) => string.Join('\t', lines) + "\n";
}

/// <summary>
/// <c>tree</c> — draw a directory as a tree.
/// </summary>
/// <remarks>
/// The box-drawing output is the point of the command, so the connectors follow the
/// original exactly: <c>├──</c> for every entry but the last in a directory, <c>└──</c> for
/// the last, and a continuation bar under any branch that still has siblings below it.
/// </remarks>
public sealed class TreeBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "tree";

    /// <inheritdoc />
    public string? LlmHint => "tree: Draw a directory tree. Supports -a, -d, --noreport.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: tree [OPTION]... [DIRECTORY]
        List the contents of a directory as a tree.

          -a           include entries whose names begin with a dot
          -d           list directories only
          --noreport   omit the file and directory count at the end
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var showAll = false;
        var directoriesOnly = false;
        var report = true;
        var operands = new List<string>();

        foreach (var argument in context.Arguments)
        {
            switch (argument)
            {
                case "--help":
                    return ExecResult.Ok(Help + "\n");

                case "--noreport":
                    report = false;
                    continue;
            }

            if (argument.Length > 1 && argument[0] == '-')
            {
                showAll |= argument.Contains('a', StringComparison.Ordinal);
                directoriesOnly |= argument.Contains('d', StringComparison.Ordinal);
                continue;
            }

            operands.Add(argument);
        }

        var root = operands.Count > 0 ? operands[0] : ".";
        var path = context.ResolvePath(root);

        if (!await context.FileSystem.ExistsAsync(path, cancellationToken))
        {
            return ExecResult.Error($"{root} [error opening dir]\n\n0 directories, 0 files\n", ExitCodes.Failure);
        }

        var output = new StringBuilder().Append(root).Append('\n');
        var counts = (Directories: 0, Files: 0);
        await WalkAsync(context, path, string.Empty, showAll, directoriesOnly, output, counts, cancellationToken);

        if (report)
        {
            var totals = await CountAsync(context, path, showAll, cancellationToken);
            output.Append('\n')
                .Append(totals.Directories).Append(totals.Directories == 1 ? " directory, " : " directories, ")
                .Append(totals.Files).Append(totals.Files == 1 ? " file\n" : " files\n");
        }

        return ExecResult.Ok(output.ToString());
    }

    private static async ValueTask WalkAsync(
        BuiltinContext context,
        VPath path,
        string prefix,
        bool showAll,
        bool directoriesOnly,
        StringBuilder output,
        (int Directories, int Files) counts,
        CancellationToken cancellationToken)
    {
        context.Budget.ChargeWork(1);
        var entries = (await context.FileSystem.ReadDirectoryAsync(path, cancellationToken))
            .Where(entry => showAll || !entry.Name.StartsWith('.'))
            .OrderBy(static entry => entry.Name, StringComparer.Ordinal)
            .ToList();

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var last = i == entries.Count - 1;
            var child = path.Join(entry.Name);
            var isDirectory = (await context.FileSystem.StatAsync(child, cancellationToken)).IsDirectory;

            if (directoriesOnly && !isDirectory)
            {
                continue;
            }

            output.Append(prefix).Append(last ? "└── " : "├── ").Append(entry.Name).Append('\n');

            if (isDirectory)
            {
                await WalkAsync(
                    context,
                    child,
                    prefix + (last ? "    " : "│   "),
                    showAll,
                    directoriesOnly,
                    output,
                    counts,
                    cancellationToken);
            }
        }
    }

    private static async ValueTask<(int Directories, int Files)> CountAsync(
        BuiltinContext context,
        VPath path,
        bool showAll,
        CancellationToken cancellationToken)
    {
        var directories = 0;
        var files = 0;

        foreach (var entry in await context.FileSystem.ReadDirectoryAsync(path, cancellationToken))
        {
            if (!showAll && entry.Name.StartsWith('.'))
            {
                continue;
            }

            var child = path.Join(entry.Name);

            if ((await context.FileSystem.StatAsync(child, cancellationToken)).IsDirectory)
            {
                directories++;
                var nested = await CountAsync(context, child, showAll, cancellationToken);
                directories += nested.Directories;
                files += nested.Files;
                continue;
            }

            files++;
        }

        return (directories, files);
    }
}

/// <summary>
/// <c>less</c> and <c>more</c> — show a file.
/// </summary>
/// <remarks>
/// There is no terminal to page against, so the whole file is written at once. That is the
/// only sensible reading of a pager in a non-interactive shell, and it keeps
/// <c>cmd | less</c> in a script working rather than hanging.
/// </remarks>
public sealed class PagerBuiltin : IBuiltin
{
    /// <summary>Creates the builtin under <paramref name="name"/>, either <c>less</c> or <c>more</c>.</summary>
    public PagerBuiltin(string name = "less") => Name = name;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string? LlmHint => $"{Name}: Show a file. Not interactive here — the whole file is written at once.";

    /// <inheritdoc />
    public string? Help => $"Usage: {Name} [OPTION]... [FILE]...\nDisplay each FILE.\n";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var operands = context.Arguments.Where(static a => a == "-" || !a.StartsWith('-')).ToList();

        if (operands.Count == 0)
        {
            return ExecResult.Ok(context.StdinText);
        }

        var output = new StringBuilder();

        foreach (var operand in operands)
        {
            try
            {
                var bytes = await context.FileSystem.ReadFileAsync(context.ResolvePath(operand), cancellationToken);
                context.Budget.ChargeWork(bytes.Length);
                output.Append(Encoding.UTF8.GetString(bytes));
            }
            catch (BashkitException)
            {
                return new ExecResult
                {
                    Stdout = StreamData.FromText(output.ToString()),
                    Stderr = StreamData.FromText($"{Name}: {operand}: No such file or directory\n"),
                    ExitCode = ExitCodes.Failure,
                };
            }
        }

        return ExecResult.Ok(output.ToString());
    }
}

/// <summary>
/// <c>watch</c> — run a command once and report what it would repeat.
/// </summary>
/// <remarks>
/// Repeating forever is not something a sandboxed script can do, so the command runs a
/// single time under the header <c>watch</c> would print. The header keeps the output
/// recognisable, and the single run keeps the script finite.
/// </remarks>
public sealed class WatchBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "watch";

    /// <inheritdoc />
    public string? LlmHint => "watch: Run a command once, with the interval header. Does not loop here.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: watch [OPTION]... COMMAND
        Run COMMAND and show its output.

          -n SECONDS    the interval watch would use (reported, not waited)
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var interval = 2.0;
        var words = new List<string>();

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            var argument = context.Arguments[i];

            if (words.Count == 0 && argument == "--help")
            {
                return ExecResult.Ok(Help + "\n");
            }

            if (words.Count == 0 && argument.StartsWith("-n", StringComparison.Ordinal))
            {
                var text = argument.Length > 2 ? argument[2..]
                    : i + 1 < context.Arguments.Count ? context.Arguments[++i] : null;

                interval = double.TryParse(text, CultureInfo.InvariantCulture, out var seconds) ? seconds : 2.0;
                continue;
            }

            if (words.Count == 0 && argument.Length > 1 && argument[0] == '-')
            {
                continue;
            }

            words.Add(argument);
        }

        if (words.Count == 0)
        {
            return ExecResult.Usage(Name, "no command given");
        }

        var command = string.Join(' ', words);
        var header = $"Every {interval.ToString("0.0", CultureInfo.InvariantCulture)}s: {command}\n\n";
        var result = await context.Hooks.RunFragment(command, context.Stdin, cancellationToken);

        return result with { Stdout = StreamData.FromText(header + result.Stdout.ToString()) };
    }
}

/// <summary>
/// <c>history</c> — the command history, which this shell does not keep.
/// </summary>
/// <remarks>
/// Retaining a transcript of every command would be a liability rather than a feature in a
/// sandbox handed to an agent, so the list is always empty and the command exists only so
/// scripts that call it do not fail.
/// </remarks>
public sealed class HistoryBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "history";

    /// <inheritdoc />
    public string? LlmHint => null;

    /// <inheritdoc />
    public string? Help => "Usage: history [-c]\nDisplay the command history, which this shell does not keep.\n";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(context.Arguments.Contains("--help") ? ExecResult.Ok(Help!) : ExecResult.Success);
}
