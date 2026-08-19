using System.Globalization;
using System.Text;

namespace Bashkit.Builtins;

/// <summary><c>realpath</c> — resolves a path to its canonical absolute form.</summary>
public sealed class RealpathBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "realpath";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);

        // Canonicalising a path is a question about the name, not about the filesystem, so
        // a missing target is not an error unless `-e` asks for one.
        var mustExist = false;
        var quiet = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-m" or "--canonicalize-missing" or "-s" or "--no-symlinks": mustExist = false; break;
                case "-q" or "--quiet": quiet = true; break;
                case "-e" or "--canonicalize-existing": mustExist = true; break;
                case "--relative-to" or "--relative-base": cursor.TakeValue(); break;
                case "-L" or "-P" or "-z": break;
                default:
                    return ExecResult.Usage("realpath", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        if (cursor.Operands.Count == 0)
        {
            return ExecResult.Usage("realpath", "missing operand", ExitCodes.Usage);
        }

        var builder = new StringBuilder();
        var errors = new StringBuilder();

        foreach (var operand in cursor.Operands)
        {
            var path = context.ResolvePath(operand);

            if (mustExist && !await context.FileSystem.ExistsAsync(path, cancellationToken))
            {
                if (!quiet)
                {
                    errors.Append("realpath: ").Append(operand).Append(": No such file or directory\n");
                }

                continue;
            }

            builder.Append(path.Value).Append('\n');
        }

        return errors.Length == 0
            ? ExecResult.Ok(builder.ToString())
            : new ExecResult
            {
                Stdout = StreamData.FromText(builder.ToString()),
                Stderr = StreamData.FromText(errors.ToString()),
                ExitCode = ExitCodes.Failure,
            };
    }
}

/// <summary><c>readlink</c> — prints a symbolic link's target.</summary>
public sealed class ReadlinkBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "readlink";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var canonicalize = false;
        var noNewline = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-f" or "-e" or "-m" or "--canonicalize": canonicalize = true; break;
                case "-n" or "--no-newline": noNewline = true; break;
                case "-q" or "-s" or "-v" or "-z": break;
                default:
                    return ExecResult.Usage("readlink", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        var builder = new StringBuilder();
        var failed = false;

        foreach (var operand in cursor.Operands)
        {
            var path = context.ResolvePath(operand);

            if (canonicalize)
            {
                builder.Append(path.Value).Append('\n');
                continue;
            }

            try
            {
                builder.Append(await context.FileSystem.ReadSymlinkAsync(path, cancellationToken)).Append('\n');
            }
            catch (FileSystemException)
            {
                failed = true;
            }
        }

        // `-n` drops the final newline, so the result can be embedded in a longer line.
        if (noNewline && builder.Length > 0 && builder[^1] == '\n')
        {
            builder.Length--;
        }

        return new ExecResult
        {
            Stdout = StreamData.FromText(builder.ToString()),
            ExitCode = failed ? ExitCodes.Failure : 0,
        };
    }
}

/// <summary><c>ln</c> — creates links. Only symbolic links exist in the virtual filesystem.</summary>
public sealed class LnBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "ln";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var symbolic = false;
        var force = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-s" or "--symbolic": symbolic = true; break;
                case "-f" or "--force": force = true; break;
                case "-n" or "-v" or "-r" or "-T": break;
                default:
                    return ExecResult.Usage("ln", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        if (cursor.Operands.Count < 2)
        {
            return ExecResult.Usage("ln", "missing file operand", ExitCodes.Failure);
        }

        var target = cursor.Operands[0];
        var linkPath = context.ResolvePath(cursor.Operands[1]);

        // Linking into a directory keeps the target's name.
        if (await CpBuiltin.IsDirectoryAsync(context, linkPath, cancellationToken))
        {
            linkPath = linkPath.Join(VPath.Parse(target).FileName);
        }

        try
        {
            if (force && await context.FileSystem.ExistsAsync(linkPath, cancellationToken))
            {
                await context.FileSystem.RemoveAsync(linkPath, recursive: false, cancellationToken);
            }

            if (symbolic)
            {
                await context.FileSystem.CreateSymlinkAsync(target, linkPath, cancellationToken);
            }
            else
            {
                // Hard links are not modelled; copying preserves the observable content,
                // and pretending otherwise would be worse than the honest approximation.
                await context.FileSystem.CopyAsync(context.ResolvePath(target), linkPath, cancellationToken);
            }
        }
        catch (FileSystemException e)
        {
            return ExecResult.Error($"ln: failed to create link '{cursor.Operands[1]}': {MkdirBuiltin.Describe(e)}\n", ExitCodes.Failure);
        }

        return ExecResult.Success;
    }
}

/// <summary><c>chmod</c> — changes permission bits.</summary>
public sealed class ChmodBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "chmod";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var recursive = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-R" or "--recursive": recursive = true; break;
                case "-v" or "-c" or "-f": break;
                default:
                    // A numeric mode looks like an option: `chmod -w file` is a symbolic
                    // mode, but `chmod 0644` never reaches here.
                    if (option.Length > 1 && (option[1] is 'r' or 'w' or 'x' or 'X' or 's' or 't'))
                    {
                        cursor.Operands.Insert(0, option);
                        break;
                    }

                    return ExecResult.Usage("chmod", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        if (cursor.Operands.Count < 2)
        {
            return ExecResult.Usage("chmod", "missing operand", ExitCodes.Failure);
        }

        var modeSpec = cursor.Operands[0];
        var errors = new StringBuilder();

        foreach (var operand in cursor.Operands.Skip(1))
        {
            await ApplyAsync(context, context.ResolvePath(operand), operand, modeSpec, recursive, errors, cancellationToken);
        }

        return errors.Length == 0 ? ExecResult.Success : ExecResult.Error(errors.ToString(), ExitCodes.Failure);
    }

    private static async ValueTask ApplyAsync(
        BuiltinContext context, VPath path, string label, string modeSpec, bool recursive, StringBuilder errors, CancellationToken cancellationToken)
    {
        FileMetadata metadata;
        try
        {
            metadata = await context.FileSystem.StatAsync(path, cancellationToken);
        }
        catch (FileSystemException)
        {
            errors.Append("chmod: cannot access '").Append(label).Append("': No such file or directory\n");
            return;
        }

        if (!TryResolveMode(modeSpec, metadata.Mode, out var mode))
        {
            errors.Append("chmod: invalid mode: '").Append(modeSpec).Append("'\n");
            return;
        }

        await context.FileSystem.ChangeModeAsync(path, mode, cancellationToken);

        if (!recursive || !metadata.IsDirectory)
        {
            return;
        }

        foreach (var entry in await context.FileSystem.ReadDirectoryAsync(path, cancellationToken))
        {
            await ApplyAsync(context, path.Join(entry.Name), label + "/" + entry.Name, modeSpec, recursive: true, errors, cancellationToken);
        }
    }

    /// <summary>Resolves an octal or symbolic mode against the file's current bits.</summary>
    internal static bool TryResolveMode(string spec, int current, out int mode)
    {
        mode = current;

        if (spec.Length == 0)
        {
            return false;
        }

        if (spec.All(static c => c is >= '0' and <= '7'))
        {
            mode = Convert.ToInt32(spec, 8);
            return true;
        }

        foreach (var clause in spec.Split(','))
        {
            if (!TryApplyClause(clause, ref mode))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryApplyClause(string clause, ref int mode)
    {
        var index = 0;
        var who = 0;

        while (index < clause.Length && clause[index] is 'u' or 'g' or 'o' or 'a')
        {
            who |= clause[index] switch
            {
                'u' => 0b111_000_000,
                'g' => 0b000_111_000,
                'o' => 0b000_000_111,
                _ => 0b111_111_111,
            };

            index++;
        }

        // No `who` means "all, masked by umask"; with no umask modelled, it means all.
        if (who == 0)
        {
            who = 0b111_111_111;
        }

        if (index >= clause.Length || clause[index] is not ('+' or '-' or '='))
        {
            return false;
        }

        var op = clause[index++];
        var bits = 0;

        while (index < clause.Length)
        {
            switch (clause[index])
            {
                case 'r': bits |= 0b100_100_100; break;
                case 'w': bits |= 0b010_010_010; break;
                case 'x' or 'X': bits |= 0b001_001_001; break;
                case 's' or 't': break;
                default: return false;
            }

            index++;
        }

        var affected = bits & who;

        mode = op switch
        {
            '+' => mode | affected,
            '-' => mode & ~affected,
            _ => (mode & ~who) | affected,
        };

        return true;
    }
}

/// <summary><c>stat</c> — reports file metadata.</summary>
public sealed class StatBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "stat";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        string? format = null;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-c" or "--format" or "--printf":
                    format = cursor.TakeValue();

                    if (format is null)
                    {
                        return ExecResult.Usage("stat", $"option requires an argument -- '{option.TrimStart('-')}'", ExitCodes.Usage);
                    }

                    break;
                case "-L" or "--dereference" or "-t" or "-f": break;
                default:
                    return ExecResult.Usage("stat", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        var builder = new StringBuilder();
        var errors = new StringBuilder();

        foreach (var operand in cursor.Operands)
        {
            FileMetadata metadata;
            try
            {
                metadata = await context.FileSystem.StatAsync(context.ResolvePath(operand), cancellationToken);
            }
            catch (FileSystemException)
            {
                errors.Append("stat: cannot statx '").Append(operand).Append("': No such file or directory\n");
                continue;
            }

            builder.Append(format is null ? Describe(operand, metadata) : Format(format, operand, metadata));
        }

        return errors.Length == 0
            ? ExecResult.Ok(builder.ToString())
            : new ExecResult
            {
                Stdout = StreamData.FromText(builder.ToString()),
                Stderr = StreamData.FromText(errors.ToString()),
                ExitCode = ExitCodes.Failure,
            };
    }

    private static string Format(string format, string name, FileMetadata metadata)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] == '\\' && i + 1 < format.Length)
            {
                builder.Append(format[++i] switch
                {
                    'n' => "\n",
                    't' => "\t",
                    _ => format[i].ToString(),
                });

                continue;
            }

            if (format[i] != '%' || i + 1 >= format.Length)
            {
                builder.Append(format[i]);
                continue;
            }

            builder.Append(format[++i] switch
            {
                'n' => name,
                's' => metadata.Size.ToString(CultureInfo.InvariantCulture),
                'f' => metadata.Mode.ToString("x", CultureInfo.InvariantCulture),
                'a' => Convert.ToString(metadata.Mode, 8),
                'A' => DescribeMode(metadata),
                'F' => metadata.Type switch
                {
                    FileType.Directory => "directory",
                    FileType.Symlink => "symbolic link",
                    FileType.Fifo => "fifo",
                    _ => "regular file",
                },
                'u' => metadata.Uid.ToString(CultureInfo.InvariantCulture),
                'g' => metadata.Gid.ToString(CultureInfo.InvariantCulture),
                'h' => metadata.LinkCount.ToString(CultureInfo.InvariantCulture),
                'Y' => metadata.ModifiedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                'X' => metadata.AccessedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                'Z' => metadata.CreatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                '%' => "%",
                var other => "%" + other,
            });
        }

        if (builder.Length == 0 || builder[^1] != '\n')
        {
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string Describe(string name, FileMetadata metadata) =>
        string.Create(CultureInfo.InvariantCulture,
            $"""
              File: {name}
              Size: {metadata.Size}	Blocks: {(metadata.Size + 511) / 512}	IO Block: 4096	{(metadata.IsDirectory ? "directory" : "regular file")}
            Access: ({Convert.ToString(metadata.Mode, 8).PadLeft(4, '0')}/{DescribeMode(metadata)})	Uid: ({metadata.Uid})	Gid: ({metadata.Gid})
            Modify: {metadata.ModifiedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}

            """);

    private static string DescribeMode(FileMetadata metadata)
    {
        var builder = new StringBuilder(10);
        builder.Append(metadata.Type switch
        {
            FileType.Directory => 'd',
            FileType.Symlink => 'l',
            FileType.Fifo => 'p',
            _ => '-',
        });

        for (var shift = 6; shift >= 0; shift -= 3)
        {
            var bits = (metadata.Mode >> shift) & 7;
            builder.Append((bits & 4) != 0 ? 'r' : '-');
            builder.Append((bits & 2) != 0 ? 'w' : '-');
            builder.Append((bits & 1) != 0 ? 'x' : '-');
        }

        return builder.ToString();
    }
}

/// <summary><c>truncate</c> — shrinks or extends a file to a given size.</summary>
public sealed class TruncateBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "truncate";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        string? sizeSpec = null;
        var noCreate = false;
        string? reference = null;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-s" or "--size": sizeSpec = cursor.TakeValue(); break;
                case "-c" or "--no-create": noCreate = true; break;
                case "-r" or "--reference": reference = cursor.TakeValue(); break;
                case "-o" or "--io-blocks": break;
                default:
                    return ExecResult.Usage("truncate", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        if (sizeSpec is null && reference is null)
        {
            return ExecResult.Usage("truncate", "you must specify either '--size' or '--reference'", ExitCodes.Failure);
        }

        if (cursor.Operands.Count == 0)
        {
            return ExecResult.Usage("truncate", "missing file operand", ExitCodes.Failure);
        }

        long referenceSize = 0;
        if (reference is not null)
        {
            try
            {
                referenceSize = (await context.FileSystem.StatAsync(context.ResolvePath(reference), cancellationToken)).Size;
            }
            catch (FileSystemException)
            {
                return ExecResult.Error($"truncate: cannot stat '{reference}': No such file or directory\n", ExitCodes.Failure);
            }
        }

        var errors = new StringBuilder();

        foreach (var operand in cursor.Operands)
        {
            var path = context.ResolvePath(operand);
            byte[] existing;

            try
            {
                existing = await context.FileSystem.ReadFileAsync(path, cancellationToken);
            }
            catch (FileSystemException)
            {
                if (noCreate)
                {
                    continue;
                }

                existing = [];
            }

            var target = sizeSpec is null ? referenceSize : ResolveSize(sizeSpec, existing.Length);

            if (target < 0)
            {
                errors.Append("truncate: invalid number: '").Append(sizeSpec).Append("'\n");
                continue;
            }

            // Extending pads with zero bytes, which is what a sparse extend looks like to
            // any reader.
            var resized = new byte[target];
            existing.AsSpan(0, (int)Math.Min(existing.Length, target)).CopyTo(resized);

            await context.FileSystem.WriteFileAsync(path, resized, cancellationToken);
        }

        return errors.Length == 0 ? ExecResult.Success : ExecResult.Error(errors.ToString(), ExitCodes.Failure);
    }

    /// <summary>Resolves a size spec, which may be absolute or relative (<c>+</c>, <c>-</c>, <c>&lt;</c>, <c>&gt;</c>).</summary>
    private static long ResolveSize(string spec, long current)
    {
        if (spec.Length == 0)
        {
            return -1;
        }

        var op = spec[0];
        var numberPart = op is '+' or '-' or '<' or '>' or '/' or '%' ? spec[1..] : spec;

        // A trailing `B` picks the decimal reading: `1K` is 1024 bytes, `1KB` is 1000.
        var scale = 1024L;

        if (numberPart.Length > 1 && char.ToUpperInvariant(numberPart[^1]) == 'B')
        {
            scale = 1000;
            numberPart = numberPart[..^1];
        }

        var multiplier = 1L;

        if (numberPart.Length > 0)
        {
            switch (char.ToUpperInvariant(numberPart[^1]))
            {
                case 'K': multiplier = scale; numberPart = numberPart[..^1]; break;
                case 'M': multiplier = scale * scale; numberPart = numberPart[..^1]; break;
                case 'G': multiplier = scale * scale * scale; numberPart = numberPart[..^1]; break;
                case 'T': multiplier = scale * scale * scale * scale; numberPart = numberPart[..^1]; break;
            }
        }

        if (!long.TryParse(numberPart, CultureInfo.InvariantCulture, out var value))
        {
            return -1;
        }

        value *= multiplier;

        return op switch
        {
            '+' => current + value,
            '-' => Math.Max(0, current - value),
            '<' => Math.Min(current, value),
            '>' => Math.Max(current, value),
            _ => value,
        };
    }
}

/// <summary><c>mktemp</c> — creates a uniquely named temporary file or directory.</summary>
public sealed class MktempBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "mktemp";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var directory = false;
        var dryRun = false;
        var tmpdir = context.State.Get("TMPDIR") ?? "/tmp";

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-d" or "--directory": directory = true; break;
                case "-u" or "--dry-run": dryRun = true; break;
                case "-p" or "--tmpdir": tmpdir = cursor.TakeValue() ?? tmpdir; break;
                case "-t" or "-q": break;
                default:
                    return ExecResult.Usage("mktemp", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        var template = cursor.Operands.Count > 0 ? cursor.Operands[0] : "tmp.XXXXXXXXXX";
        var basePath = template.Contains('/', StringComparison.Ordinal)
            ? context.ResolvePath(template)
            : VPath.Parse(tmpdir).Join(template);

        // Uniqueness comes from probing the virtual filesystem, not from randomness: the
        // sandbox must be reproducible, and the tree is the only thing that can collide.
        var name = basePath.FileName;
        var xCount = name.Length - name.TrimEnd('X').Length;
        var prefix = xCount > 0 ? name[..^xCount] : name;
        var parent = basePath.Parent ?? VPath.Root;

        for (var attempt = 0; attempt < 100_000; attempt++)
        {
            var suffix = attempt.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(xCount, 6), '0');
            var candidate = parent.Join(prefix + suffix);

            if (await context.FileSystem.ExistsAsync(candidate, cancellationToken))
            {
                continue;
            }

            if (!dryRun)
            {
                try
                {
                    if (directory)
                    {
                        await context.FileSystem.CreateDirectoryAsync(candidate, recursive: true, cancellationToken);
                    }
                    else
                    {
                        await context.FileSystem.CreateDirectoryAsync(parent, recursive: true, cancellationToken);
                        await context.FileSystem.WriteFileAsync(candidate, ReadOnlyMemory<byte>.Empty, cancellationToken);
                    }
                }
                catch (FileSystemException e)
                {
                    return ExecResult.Error($"mktemp: failed to create file: {MkdirBuiltin.Describe(e)}\n", ExitCodes.Failure);
                }
            }

            return ExecResult.Ok(candidate.Value + "\n");
        }

        return ExecResult.Error("mktemp: failed to create a unique name\n", ExitCodes.Failure);
    }
}

/// <summary><c>rmdir</c> — removes empty directories.</summary>
public sealed class RmdirBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "rmdir";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var parents = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-p" or "--parents": parents = true; break;
                case "-v" or "--ignore-fail-on-non-empty": break;
                default:
                    return ExecResult.Usage("rmdir", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        if (cursor.Operands.Count == 0)
        {
            return ExecResult.Usage("rmdir", "missing operand", ExitCodes.Failure);
        }

        var errors = new StringBuilder();

        foreach (var operand in cursor.Operands)
        {
            var path = context.ResolvePath(operand);

            while (true)
            {
                try
                {
                    await context.FileSystem.RemoveAsync(path, recursive: false, cancellationToken);
                }
                catch (FileSystemException e)
                {
                    errors.Append("rmdir: failed to remove '").Append(operand).Append("': ")
                        .Append(MkdirBuiltin.Describe(e)).Append('\n');
                    break;
                }

                if (!parents || path.Parent is not { } next || next.IsRoot)
                {
                    break;
                }

                path = next;
            }
        }

        return errors.Length == 0 ? ExecResult.Success : ExecResult.Error(errors.ToString(), ExitCodes.Failure);
    }
}
