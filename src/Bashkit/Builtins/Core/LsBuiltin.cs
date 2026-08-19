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
    public string? Help =>
        """
        Usage: ls [OPTION]... [FILE]...
        List information about the FILEs, the current directory by default.

          -l    use a long listing format
          -a    do not ignore entries starting with .
          -R    list subdirectories recursively
          -1    list one file per line
          -d    list directories themselves, not their contents
          -r    reverse the order
          -t    sort by modification time
          -S    sort by size
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (CommandHelp.Handle(context.Arguments, Help!, "ls 0.1.0") is { } help)
        {
            return help;
        }

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
        var classify = false;
        var columns = false;
        var unsupported = new List<string>();

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
                case "-t": sortByTime = true; break;
                case "-S": sortBySize = true; break;
                case "-F" or "--classify": classify = true; break;
                case "-C": columns = true; break;
                case "-h" or "--human-readable" or "-p" or "-i" or "-n" or "-G" or "--color":
                    break;

                // Options real ls has and this one does not. Naming them beats a generic
                // "invalid option", which would suggest the flag does not exist at all.
                case "-r" or "--reverse": unsupported.Add("reverse"); break;
                case "-Q" or "--quote-name": unsupported.Add("quote-name"); break;
                case "--quoting-style": cursor.TakeValue(); unsupported.Add("quoting-style"); break;
                case "--group-directories-first": unsupported.Add("group-directories-first"); break;
                case "--sort": cursor.TakeValue(); unsupported.Add("sort"); break;
                case "--time-style": cursor.TakeValue(); unsupported.Add("time-style"); break;

                default:
                    return ExecResult.Usage("ls", $"invalid option -- '{option.TrimStart('-')}'", ExitCodes.Usage);
            }
        }

        if (unsupported.Count > 0)
        {
            return ExecResult.Ok($"ls: option(s) not yet implemented in bashkit: {string.Join(", ", unsupported)}\n")
                with { ExitCode = ExitCodes.Usage };
        }

        var operands = cursor.Operands.Count == 0 ? ["."] : cursor.Operands;
        var builder = new StringBuilder();
        var errors = new StringBuilder();
        var state = new ListingState();
        var options = new Options(
            longFormat, showHidden, almostAll, onePerLine, recursive, reverse, sortByTime, sortBySize,
            classify, columns);

        // ls lists the plain files first, then each directory — whatever order the operands
        // were given in.
        var files = new List<(string Operand, FileMetadata Metadata)>();
        var directories = new List<string>();

        foreach (var operand in operands)
        {
            FileMetadata metadata;

            try
            {
                metadata = await context.FileSystem.StatAsync(context.ResolvePath(operand), cancellationToken);
            }
            catch (FileSystemException)
            {
                errors.Append("ls: cannot access '").Append(operand).Append("': No such file or directory\n");
                continue;
            }

            if (!metadata.IsDirectory || directoryItself)
            {
                files.Add((operand, metadata));
                continue;
            }

            directories.Add(operand);
        }

        Sort(files, options);

        foreach (var (operand, metadata) in files)
        {
            builder.Append(longFormat ? FormatLong(operand, metadata, classify) : Decorate(operand, metadata, classify) + "\n");
            state.AnyEmitted = true;
        }

        var multiple = operands.Count > 1 || recursive;

        foreach (var operand in directories)
        {
            await ListDirectoryAsync(
                context, context.ResolvePath(operand), operand, builder, multiple, state, options, cancellationToken);
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
        bool Recursive, bool Reverse, bool SortByTime, bool SortBySize,
        bool Classify, bool Columns);

    /// <summary>Orders entries by whichever key the flags selected.</summary>
    private static void Sort(List<(string Name, FileMetadata Metadata)> rows, Options options)
    {
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
    }

    /// <summary>
    /// Appends the <c>-F</c> suffix that says what kind of thing an entry is.
    /// </summary>
    private static string Decorate(string name, FileMetadata metadata, bool classify)
    {
        if (!classify)
        {
            return name;
        }

        return metadata.Type switch
        {
            FileType.Directory => name + "/",
            FileType.Symlink => name + "@",
            FileType.Fifo => name + "|",
            _ => (metadata.Mode & 0b001_001_001) != 0 ? name + "*" : name,
        };
    }

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

        Sort(rows, options);

        // `ls -a` shows the two synthetic entries; the virtual filesystem does not store
        // them, so they are produced here.
        if (options.ShowHidden)
        {
            builder.Append(options.LongFormat ? FormatLong(".", DirectoryMetadata, options.Classify) : ".\n");
            builder.Append(options.LongFormat ? FormatLong("..", DirectoryMetadata, options.Classify) : "..\n");
        }

        if (options.LongFormat)
        {
            builder.Append("total ").Append(rows.Sum(static r => (r.Metadata.Size + 1023) / 1024)
                .ToString(CultureInfo.InvariantCulture)).Append('\n');

            foreach (var (name, metadata) in rows)
            {
                builder.Append(FormatLong(name, metadata, options.Classify));
            }
        }
        else if (options.Columns && !options.OnePerLine)
        {
            // `-C` packs the names across the line; two spaces between columns is what ls
            // uses when the terminal width is unknown.
            builder.Append(string.Join("  ", rows.Select(r => Decorate(r.Name, r.Metadata, options.Classify))))
                .Append('\n');
        }
        else
        {
            foreach (var (name, metadata) in rows)
            {
                builder.Append(Decorate(name, metadata, options.Classify)).Append('\n');
            }
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

    private static string FormatLong(string name, FileMetadata metadata, bool classify)
    {
        var type = metadata.Type switch
        {
            FileType.Directory => 'd',
            FileType.Symlink => 'l',
            FileType.Fifo => 'p',
            _ => '-',
        };

        var display = Decorate(name, metadata, classify);

        return string.Create(CultureInfo.InvariantCulture,
            $"{type}{FormatMode(metadata.Mode)} {metadata.LinkCount,3} {"user",-8} {"user",-8} {metadata.Size,8} {metadata.ModifiedAt.UtcDateTime:MMM dd HH:mm} {display}\n");
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
