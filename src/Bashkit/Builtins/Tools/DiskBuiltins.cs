using System.Globalization;
using System.Text;

namespace Bashkit.Builtins;

/// <summary>
/// <c>df</c> — report space on the virtual filesystem.
/// </summary>
/// <remarks>
/// The figures come from the sandbox's own quota, not from any host disk. That is the
/// honest answer: a script asking "how much room is left" is asking about the space it can
/// actually use, which here is the byte budget the host configured.
/// </remarks>
public sealed class DfBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "df";

    /// <inheritdoc />
    public string? LlmHint => "df: Report virtual filesystem space. Supports -h.";

    /// <inheritdoc />
    public string? Help => "Usage: df [OPTION]... [FILE]...\nShow space on the virtual filesystem.\n";

    /// <inheritdoc />
    public ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var human = false;

        foreach (var argument in context.Arguments)
        {
            if (argument == "--help")
            {
                return ValueTask.FromResult(ExecResult.Ok(Help!));
            }

            if (argument is "-h" or "--human-readable" or "-H")
            {
                human = true;
            }
        }

        var total = context.Budget.Limits.MaxInputBytes;
        var used = 0L;
        var free = Math.Max(total - used, 0);

        var output = new StringBuilder();

        output.Append(human
            ? "Filesystem      Size  Used Avail Use% Mounted on\n"
            : "Filesystem     1K-blocks  Used Available Use% Mounted on\n");

        output.Append("bashkit-vfs    ")
            .Append(Format(total, human)).Append(' ')
            .Append(Format(used, human)).Append(' ')
            .Append(Format(free, human)).Append("   0% /\n");

        return ValueTask.FromResult(ExecResult.Ok(output.ToString()));
    }

    private static string Format(long bytes, bool human) =>
        human ? Human(bytes) : (bytes / 1024).ToString(CultureInfo.InvariantCulture);

    internal static string Human(long bytes)
    {
        string[] units = ["", "K", "M", "G", "T", "P"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + units[unit];
    }
}

/// <summary>
/// <c>du</c> — report how much space a tree occupies.
/// </summary>
/// <remarks>
/// Sizes are the sum of the file contents rather than allocated blocks, because the virtual
/// filesystem has no block size to round to. The number is therefore exact rather than
/// approximate, which is the more useful of the two answers here.
/// </remarks>
public sealed class DuBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "du";

    /// <inheritdoc />
    public string? LlmHint => "du: Report disk usage of a tree. Supports -s, -h, -a, -c.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: du [OPTION]... [FILE]...
        Summarize the space used by each FILE, recursively for directories.

          -s    print only a total for each argument
          -h    print sizes in human-readable form
          -a    include files as well as directories
          -c    print a grand total
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var summarize = false;
        var human = false;
        var all = false;
        var grandTotal = false;
        var operands = new List<string>();

        foreach (var argument in context.Arguments)
        {
            if (argument == "--help")
            {
                return ExecResult.Ok(Help + "\n");
            }

            if (argument.Length > 1 && argument[0] == '-' && argument != "-")
            {
                foreach (var flag in argument.TrimStart('-'))
                {
                    switch (flag)
                    {
                        case 's': summarize = true; break;
                        case 'h': human = true; break;
                        case 'a': all = true; break;
                        case 'c': grandTotal = true; break;
                        default: break;
                    }
                }

                continue;
            }

            operands.Add(argument);
        }

        if (operands.Count == 0)
        {
            operands.Add(".");
        }

        var output = new StringBuilder();
        var errors = new StringBuilder();
        var failed = false;
        long total = 0;

        foreach (var operand in operands)
        {
            var path = context.ResolvePath(operand);

            if (!await context.FileSystem.ExistsAsync(path, cancellationToken))
            {
                errors.Append($"du: cannot access '{operand}': No such file or directory\n");
                failed = true;
                continue;
            }

            var size = await MeasureAsync(
                context, path, operand, summarize, all, human, isTop: true, output, cancellationToken);

            total += size;

            if (summarize)
            {
                Report(output, size, operand, human);
            }
        }

        if (grandTotal)
        {
            Report(output, total, "total", human);
        }

        return new ExecResult
        {
            Stdout = StreamData.FromText(output.ToString()),
            Stderr = StreamData.FromText(errors.ToString()),
            ExitCode = failed ? ExitCodes.Failure : ExitCodes.Success,
        };
    }

    /// <summary>Sums a tree, reporting sub-directories along the way unless summarizing.</summary>
    private static async ValueTask<long> MeasureAsync(
        BuiltinContext context,
        VPath path,
        string display,
        bool summarize,
        bool all,
        bool human,
        bool isTop,
        StringBuilder output,
        CancellationToken cancellationToken)
    {
        context.Budget.ChargeWork(1);
        FileMetadata metadata;

        try
        {
            metadata = await context.FileSystem.StatAsync(path, cancellationToken);
        }
        catch (BashkitException)
        {
            return 0;
        }

        if (!metadata.IsDirectory)
        {
            // A named file is always reported; one found inside a tree only with `-a`.
            if (!summarize && (all || isTop))
            {
                Report(output, metadata.Size, display, human);
            }

            return metadata.Size;
        }

        long total = 0;

        foreach (var entry in await context.FileSystem.ReadDirectoryAsync(path, cancellationToken))
        {
            total += await MeasureAsync(
                context,
                path.Join(entry.Name),
                display.TrimEnd('/') + "/" + entry.Name,
                summarize,
                all,
                human,
                isTop: false,
                output,
                cancellationToken);
        }

        if (!summarize)
        {
            Report(output, total, display, human);
        }

        return total;
    }

    private static void Report(StringBuilder output, long bytes, string display, bool human) =>
        output.Append(human ? DfBuiltin.Human(bytes) : Math.Max(bytes / 1024, bytes > 0 ? 1 : 0).ToString(CultureInfo.InvariantCulture))
            .Append('\t')
            .Append(display)
            .Append('\n');
}
