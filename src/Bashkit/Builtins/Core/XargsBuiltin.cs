using System.Globalization;
using System.Text;

namespace Bashkit.Builtins;

/// <summary>
/// <c>xargs</c> — builds command lines from standard input.
/// </summary>
/// <remarks>
/// Commands are dispatched through the shell hooks, so <c>xargs</c> can only invoke
/// registered builtins. There is no way for it to become an escape hatch to the host.
/// </remarks>
public sealed class XargsBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "xargs";

    /// <inheritdoc />
    public string? LlmHint => "xargs: Builds commands from stdin. Supports -n -I -0 -d -r -t -a.";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var maxArgs = 0;
        string? replace = null;
        var nullSeparated = false;
        string? delimiter = null;
        var noRunIfEmpty = false;
        var trace = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-n" or "--max-args":
                    int.TryParse(cursor.TakeValue(), CultureInfo.InvariantCulture, out maxArgs);
                    break;

                case "-I" or "--replace" or "-i":
                    replace = cursor.TakeValue() ?? "{}";
                    break;

                case "-0" or "--null": nullSeparated = true; break;
                case "-d" or "--delimiter": delimiter = cursor.TakeValue(); break;
                case "-r" or "--no-run-if-empty": noRunIfEmpty = true; break;
                case "-t" or "--verbose": trace = true; break;
                case "-P" or "--max-procs" or "-s" or "--max-chars" or "-L": cursor.TakeValue(); break;
                case "-x" or "-p": break;
                default:
                    return ExecResult.Usage("xargs", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        var items = Split(context.StdinText, nullSeparated, delimiter);

        // Default command is `echo`, which is what makes `xargs` alone a joiner.
        var template = cursor.Operands.Count > 0 ? cursor.Operands : ["echo"];

        if (items.Count == 0)
        {
            return noRunIfEmpty || replace is not null
                ? ExecResult.Success
                : await RunAsync(context, template, trace, cancellationToken);
        }

        var output = new StringBuilder();
        var errors = new StringBuilder();
        var exitCode = 0;

        if (replace is not null)
        {
            // `-I` runs once per item, substituting into every word of the template.
            foreach (var item in items)
            {
                var words = template.Select(word => word.Replace(replace, item, StringComparison.Ordinal)).ToList();
                var result = await RunAsync(context, words, trace, cancellationToken);
                output.Append(result.Stdout.ToString());
                errors.Append(result.Stderr.ToString());
                exitCode = result.ExitCode;
            }

            return Combine(output, errors, exitCode);
        }

        var batchSize = maxArgs > 0 ? maxArgs : items.Count;

        for (var offset = 0; offset < items.Count; offset += batchSize)
        {
            var words = new List<string>(template);
            words.AddRange(items.Skip(offset).Take(batchSize));

            var result = await RunAsync(context, words, trace, cancellationToken);
            output.Append(result.Stdout.ToString());
            errors.Append(result.Stderr.ToString());
            exitCode = result.ExitCode;
        }

        return Combine(output, errors, exitCode);
    }

    private static ExecResult Combine(StringBuilder output, StringBuilder errors, int exitCode) => new()
    {
        Stdout = StreamData.FromText(output.ToString()),
        Stderr = StreamData.FromText(errors.ToString()),
        ExitCode = exitCode,
    };

    private static async ValueTask<ExecResult> RunAsync(
        BuiltinContext context, IReadOnlyList<string> words, bool trace, CancellationToken cancellationToken)
    {
        var result = await context.Hooks.RunCommand(words, null, cancellationToken);

        return trace
            ? result with { Stderr = StreamData.Concat(StreamData.FromText(string.Join(' ', words) + "\n"), result.Stderr) }
            : result;
    }

    private static List<string> Split(string input, bool nullSeparated, string? delimiter)
    {
        if (input.Length == 0)
        {
            return [];
        }

        if (nullSeparated)
        {
            return [.. input.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        }

        if (delimiter is { Length: > 0 })
        {
            var separator = delimiter == "\\n" ? "\n" : delimiter == "\\t" ? "\t" : delimiter;
            return [.. input.Split(separator, StringSplitOptions.RemoveEmptyEntries)
                .Select(static piece => piece.TrimEnd('\n'))];
        }

        // Default splitting is on whitespace, honouring quotes the way xargs does.
        var items = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';

        foreach (var c in input)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    items.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            items.Add(current.ToString());
        }

        return items;
    }
}
