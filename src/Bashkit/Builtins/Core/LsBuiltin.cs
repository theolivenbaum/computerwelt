using System.Globalization;
using System.Text;

namespace Bashkit.Builtins;

/// <summary><c>ls</c> — lists directory contents.</summary>
public sealed class LsBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "ls";

    /// <inheritdoc />
    public string? LlmHint => "ls: Lists files. Supports -l (long), -a (all), -R (recursive), -1, -d, -r, -t, -S.";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var longFormat = false;
        var showHidden = false;
        var almostAll = false;
        var onePerLine = false;
        var recursive = false;
        var directoryItself = false;
        var reverse = false;
        var sortByTime = false;
        var sortBySize = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-l" or "--long": longFormat = true; break;
                case "-a" or "--all": showHidden = true; break;
                case "-A" or "--almost-all": almostAll = true; break;
                case "-1": onePerLine = true; break;
                case "-R" or "--recursive": recursive = true; break;
                case "-d" or "--directory": directoryItself = true; break;
                case "-r" or "--reverse": reverse = true; break;
                case "-t": sortByTime = true; break;
                case "-S": sortBySize = true; break;
                case "-h" or "--human-readable" or "-F" or "--classify" or "-C" or "-p" or "-i" or "-n" or "-G" or "--color":
                    break;

                default:
                    return ExecResult.Usage("ls", $"invalid option -- '{option.TrimStart('-')}'", ExitCodes.Usage);
            }
        }

        var operands = cursor.Operands.Count == 0 ? ["."] : cursor.Operands;
        var builder = new StringBuilder();
        var errors = new StringBuilder();
        var multiple = operands.Count > 1 || recursive;
        var state = new ListingState();

        foreach (var operand in operands)
        {
            var path = context.ResolvePath(operand);

            FileMetadata metadata;
            try
            {
                metadata = await context.FileSystem.StatAsync(path, cancellationToken);
            }
            catch (FileSystemException)
            {
                errors.Append("ls: cannot access '").Append(operand).Append("': No such file or directory\n");
                continue;
            }

            if (!metadata.IsDirectory || directoryItself)
            {
                builder.Append(longFormat ? FormatLong(operand, metadata) : operand + "\n");
                continue;
            }

            await ListDirectoryAsync(
                context, path, operand, builder, multiple, state,
                new Options(longFormat, showHidden, almostAll, onePerLine, recursive, reverse, sortByTime, sortBySize),
                cancellationToken);
        }

        return errors.Length == 0
            ? ExecResult.Ok(builder.ToString())
            : new ExecResult
            {
                Stdout = StreamData.FromText(builder.ToString()),
                Stderr = StreamData.FromText(errors.ToString()),
                ExitCode = ExitCodes.Usage,
            };
    }

    /// <summary>
    /// Tracks whether any listing has been emitted yet, so that a blank line separates
    /// sections without leading the output. An async method cannot take this by
    /// <c>ref</c>, so it is carried as a small mutable object.
    /// </summary>
    private sealed class ListingState
    {
        public bool AnyEmitted { get; set; }
    }

    private readonly record struct Options(
        bool LongFormat, bool ShowHidden, bool AlmostAll, bool OnePerLine,
        bool Recursive, bool Reverse, bool SortByTime, bool SortBySize);

    private static async ValueTask ListDirectoryAsync(
        BuiltinContext context,
        VPath path,
        string display,
        StringBuilder builder,
        bool showHeader,
        ListingState state,
        Options options,
        CancellationToken cancellationToken)
    {
        if (showHeader)
        {
            if (state.AnyEmitted)
            {
                builder.Append('\n');
            }

            builder.Append(display).Append(":\n");
        }

        state.AnyEmitted = true;

        var entries = (await context.FileSystem.ReadDirectoryAsync(path, cancellationToken)).ToList();

        if (!options.ShowHidden && !options.AlmostAll)
        {
            entries.RemoveAll(static e => e.Name.StartsWith('.'));
        }

        var rows = new List<(string Name, FileMetadata Metadata)>();

        foreach (var entry in entries)
        {
            FileMetadata metadata;
            try
            {
                metadata = await context.FileSystem.StatLinkAsync(path.Join(entry.Name), cancellationToken);
            }
            catch (FileSystemException)
            {
                continue;
            }

            rows.Add((entry.Name, metadata));
        }

        rows.Sort((a, b) =>
        {
            var result = options.SortByTime ? b.Metadata.ModifiedAt.CompareTo(a.Metadata.ModifiedAt)
                : options.SortBySize ? b.Metadata.Size.CompareTo(a.Metadata.Size)
                : string.CompareOrdinal(a.Name, b.Name);

            return result != 0 ? result : string.CompareOrdinal(a.Name, b.Name);
        });

        if (options.Reverse)
        {
            rows.Reverse();
        }

        // `ls -a` shows the two synthetic entries; the virtual filesystem does not store
        // them, so they are produced here.
        if (options.ShowHidden)
        {
            builder.Append(options.LongFormat ? FormatLong(".", DirectoryMetadata) : ".\n");
            builder.Append(options.LongFormat ? FormatLong("..", DirectoryMetadata) : "..\n");
        }

        if (options.LongFormat)
        {
            builder.Append("total ").Append(rows.Sum(static r => (r.Metadata.Size + 1023) / 1024)
                .ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        foreach (var (name, metadata) in rows)
        {
            builder.Append(options.LongFormat ? FormatLong(name, metadata) : name + "\n");
        }

        if (!options.Recursive)
        {
            return;
        }

        foreach (var (name, metadata) in rows)
        {
            if (metadata.IsDirectory)
            {
                await ListDirectoryAsync(
                    context, path.Join(name), display + "/" + name, builder,
                    showHeader: true, state, options, cancellationToken);
            }
        }
    }

    private static readonly FileMetadata DirectoryMetadata = new()
    {
        Type = FileType.Directory,
        Mode = 0b111_101_101,
        Size = 4096,
    };

    private static string FormatLong(string name, FileMetadata metadata)
    {
        var type = metadata.Type switch
        {
            FileType.Directory => 'd',
            FileType.Symlink => 'l',
            FileType.Fifo => 'p',
            _ => '-',
        };

        return string.Create(CultureInfo.InvariantCulture,
            $"{type}{FormatMode(metadata.Mode)} {metadata.LinkCount,3} {"user",-8} {"user",-8} {metadata.Size,8} {metadata.ModifiedAt.UtcDateTime:MMM dd HH:mm} {name}\n");
    }

    private static string FormatMode(int mode)
    {
        var builder = new StringBuilder(9);
        for (var shift = 6; shift >= 0; shift -= 3)
        {
            var bits = (mode >> shift) & 7;
            builder.Append((bits & 4) != 0 ? 'r' : '-');
            builder.Append((bits & 2) != 0 ? 'w' : '-');
            builder.Append((bits & 1) != 0 ? 'x' : '-');
        }

        return builder.ToString();
    }
}
