using System.Text;

namespace Bashkit.Builtins;

/// <summary><c>cat</c> — concatenates files to standard output.</summary>
public sealed class CatBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "cat";

    /// <inheritdoc />
    public string? LlmHint => "cat: Prints file contents. Supports -n (number lines), -A/-E (show ends), -s (squeeze blanks).";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: cat [OPTION]... [FILE]...
        Concatenate FILE(s) to standard output.

          -n    number every output line
          -b    number non-blank output lines
          -E    display $ at the end of each line
          -s    squeeze repeated blank lines
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        if (CommandHelp.Handle(context.Arguments, Help!, "cat 0.1.0") is { } help)
        {
            return help;
        }

        var cursor = new ArgCursor(context.Arguments);
        var numberAll = false;
        var numberNonBlank = false;
        var showEnds = false;
        var squeezeBlank = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-n" or "--number": numberAll = true; break;
                case "-b" or "--number-nonblank": numberNonBlank = true; break;
                case "-E" or "--show-ends": showEnds = true; break;
                case "-s" or "--squeeze-blank": squeezeBlank = true; break;
                case "-A" or "--show-all": showEnds = true; break;
                case "-u" or "-v" or "-T" or "-t" or "-e": break;
                default:
                    return ExecResult.Usage("cat", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        var builder = new StringBuilder();

        foreach (var operand in cursor.Operands.Count == 0 ? ["-"] : cursor.Operands)
        {
            if (operand == "-")
            {
                builder.Append(context.StdinText);
                continue;
            }

            try
            {
                var bytes = await context.FileSystem.ReadFileAsync(context.ResolvePath(operand), cancellationToken);
                builder.Append(Encoding.UTF8.GetString(bytes));
            }
            catch (FileSystemException e)
            {
                return ExecResult.Error($"cat: {e.Message}\n", ExitCodes.Failure);
            }
        }

        var text = builder.ToString();

        if (!numberAll && !numberNonBlank && !showEnds && !squeezeBlank)
        {
            return ExecResult.Ok(text);
        }

        return ExecResult.Ok(Format(text, numberAll, numberNonBlank, showEnds, squeezeBlank));
    }

    private static string Format(string text, bool numberAll, bool numberNonBlank, bool showEnds, bool squeezeBlank)
    {
        var endsWithNewline = text.EndsWith('\n');
        var lines = text.Split('\n');
        if (endsWithNewline && lines.Length > 0)
        {
            lines = lines[..^1];
        }

        var builder = new StringBuilder();
        var number = 1;
        var previousBlank = false;

        foreach (var line in lines)
        {
            var blank = line.Length == 0;

            if (squeezeBlank && blank && previousBlank)
            {
                continue;
            }

            previousBlank = blank;

            // `-b` numbers only non-blank lines and overrides `-n`.
            if (numberNonBlank)
            {
                if (!blank)
                {
                    builder.Append(number++.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(6)).Append("\t");
                }
            }
            else if (numberAll)
            {
                builder.Append(number++.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(6)).Append("\t");
            }

            builder.Append(line);

            if (showEnds)
            {
                builder.Append('$');
            }

            builder.Append('\n');
        }

        var result = builder.ToString();
        return endsWithNewline || result.Length == 0 ? result : result.TrimEnd('\n');
    }
}

/// <summary><c>mkdir</c> — creates directories.</summary>
public sealed class MkdirBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "mkdir";

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
                case "-v" or "--verbose": break;
                case "-m" or "--mode": cursor.TakeValue(); break;
                default:
                    return ExecResult.Usage("mkdir", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        if (cursor.Operands.Count == 0)
        {
            return ExecResult.Usage("mkdir", "missing operand", ExitCodes.Failure);
        }

        var errors = new StringBuilder();

        foreach (var operand in cursor.Operands)
        {
            try
            {
                await context.FileSystem.CreateDirectoryAsync(context.ResolvePath(operand), parents, cancellationToken);
            }
            catch (FileSystemException e)
            {
                errors.Append("mkdir: cannot create directory '").Append(operand).Append("': ")
                    .Append(Describe(e)).Append('\n');
            }
        }

        return errors.Length == 0 ? ExecResult.Success : ExecResult.Error(errors.ToString(), ExitCodes.Failure);
    }

    internal static string Describe(FileSystemException e) => e.FsKind switch
    {
        FileSystemErrorKind.AlreadyExists => "File exists",
        FileSystemErrorKind.NotFound => "No such file or directory",
        FileSystemErrorKind.NotADirectory => "Not a directory",
        FileSystemErrorKind.DirectoryNotEmpty => "Directory not empty",
        FileSystemErrorKind.PermissionDenied => "Permission denied",
        _ => e.Message,
    };
}

/// <summary><c>rm</c> — removes files and directories.</summary>
public sealed class RmBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "rm";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var recursive = false;
        var force = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-r" or "-R" or "--recursive": recursive = true; break;
                case "-f" or "--force": force = true; break;
                case "-v" or "--verbose" or "-i" or "-d": break;
                default:
                    return ExecResult.Usage("rm", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        if (cursor.Operands.Count == 0)
        {
            return force ? ExecResult.Success : ExecResult.Usage("rm", "missing operand", ExitCodes.Failure);
        }

        var errors = new StringBuilder();

        foreach (var operand in cursor.Operands)
        {
            var path = context.ResolvePath(operand);

            try
            {
                var metadata = await context.FileSystem.StatLinkAsync(path, cancellationToken);
                if (metadata.IsDirectory && !recursive)
                {
                    errors.Append("rm: cannot remove '").Append(operand).Append("': Is a directory\n");
                    continue;
                }

                await context.FileSystem.RemoveAsync(path, recursive, cancellationToken);
            }
            catch (FileSystemException e)
            {
                if (!force)
                {
                    errors.Append("rm: cannot remove '").Append(operand).Append("': ")
                        .Append(MkdirBuiltin.Describe(e)).Append('\n');
                }
            }
        }

        return errors.Length == 0 ? ExecResult.Success : ExecResult.Error(errors.ToString(), ExitCodes.Failure);
    }
}

/// <summary><c>touch</c> — creates empty files or updates timestamps.</summary>
public sealed class TouchBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "touch";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var noCreate = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-c" or "--no-create": noCreate = true; break;
                case "-a" or "-m": break;
                case "-d" or "-t" or "-r": cursor.TakeValue(); break;
                default:
                    return ExecResult.Usage("touch", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        if (cursor.Operands.Count == 0)
        {
            return ExecResult.Usage("touch", "missing file operand", ExitCodes.Failure);
        }

        var errors = new StringBuilder();
        var now = DateTimeOffset.UtcNow;

        foreach (var operand in cursor.Operands)
        {
            var path = context.ResolvePath(operand);

            try
            {
                if (await context.FileSystem.ExistsAsync(path, cancellationToken))
                {
                    await context.FileSystem.SetModifiedTimeAsync(path, now, cancellationToken);
                    continue;
                }

                if (noCreate)
                {
                    continue;
                }

                await context.FileSystem.WriteFileAsync(path, ReadOnlyMemory<byte>.Empty, cancellationToken);
            }
            catch (FileSystemException e)
            {
                errors.Append("touch: cannot touch '").Append(operand).Append("': ")
                    .Append(MkdirBuiltin.Describe(e)).Append('\n');
            }
        }

        return errors.Length == 0 ? ExecResult.Success : ExecResult.Error(errors.ToString(), ExitCodes.Failure);
    }
}

/// <summary><c>cp</c> — copies files and directory trees.</summary>
public sealed class CpBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "cp";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);
        var recursive = false;

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-r" or "-R" or "--recursive" or "-a": recursive = true; break;
                case "-f" or "-v" or "-p" or "-n" or "-i" or "-t": break;
                default:
                    return ExecResult.Usage("cp", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        if (cursor.Operands.Count < 2)
        {
            return ExecResult.Usage("cp", "missing destination file operand", ExitCodes.Failure);
        }

        var sources = cursor.Operands[..^1];
        var destination = context.ResolvePath(cursor.Operands[^1]);
        var destinationIsDirectory = await IsDirectoryAsync(context, destination, cancellationToken);

        if (sources.Count > 1 && !destinationIsDirectory)
        {
            return ExecResult.Error($"cp: target '{cursor.Operands[^1]}' is not a directory\n", ExitCodes.Failure);
        }

        var errors = new StringBuilder();

        foreach (var source in sources)
        {
            var from = context.ResolvePath(source);
            var to = destinationIsDirectory ? destination.Join(from.FileName) : destination;

            try
            {
                await CopyAsync(context, from, to, recursive, cancellationToken);
            }
            catch (FileSystemException e)
            {
                errors.Append("cp: cannot copy '").Append(source).Append("': ")
                    .Append(MkdirBuiltin.Describe(e)).Append('\n');
            }
        }

        return errors.Length == 0 ? ExecResult.Success : ExecResult.Error(errors.ToString(), ExitCodes.Failure);
    }

    private static async ValueTask CopyAsync(BuiltinContext context, VPath from, VPath to, bool recursive, CancellationToken cancellationToken)
    {
        var metadata = await context.FileSystem.StatAsync(from, cancellationToken);

        if (!metadata.IsDirectory)
        {
            await context.FileSystem.CopyAsync(from, to, cancellationToken);
            return;
        }

        if (!recursive)
        {
            throw new FileSystemException(FileSystemErrorKind.IsADirectory, $"{from}: Is a directory");
        }

        await context.FileSystem.CreateDirectoryAsync(to, recursive: true, cancellationToken);

        foreach (var entry in await context.FileSystem.ReadDirectoryAsync(from, cancellationToken))
        {
            await CopyAsync(context, from.Join(entry.Name), to.Join(entry.Name), recursive: true, cancellationToken);
        }
    }

    internal static async ValueTask<bool> IsDirectoryAsync(BuiltinContext context, VPath path, CancellationToken cancellationToken)
    {
        try
        {
            return (await context.FileSystem.StatAsync(path, cancellationToken)).IsDirectory;
        }
        catch (FileSystemException)
        {
            return false;
        }
    }
}

/// <summary><c>mv</c> — moves or renames files.</summary>
public sealed class MvBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "mv";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var cursor = new ArgCursor(context.Arguments);

        while (cursor.NextOption() is { } option)
        {
            switch (option)
            {
                case "-f" or "-v" or "-n" or "-i": break;
                default:
                    return ExecResult.Usage("mv", $"invalid option -- '{option.TrimStart('-')}'");
            }
        }

        if (cursor.Operands.Count < 2)
        {
            return ExecResult.Usage("mv", "missing destination file operand", ExitCodes.Failure);
        }

        var sources = cursor.Operands[..^1];
        var destination = context.ResolvePath(cursor.Operands[^1]);
        var destinationIsDirectory = await CpBuiltin.IsDirectoryAsync(context, destination, cancellationToken);

        var errors = new StringBuilder();

        foreach (var source in sources)
        {
            var from = context.ResolvePath(source);
            var to = destinationIsDirectory ? destination.Join(from.FileName) : destination;

            try
            {
                await context.FileSystem.RenameAsync(from, to, cancellationToken);
            }
            catch (FileSystemException e)
            {
                errors.Append("mv: cannot move '").Append(source).Append("': ")
                    .Append(MkdirBuiltin.Describe(e)).Append('\n');
            }
        }

        return errors.Length == 0 ? ExecResult.Success : ExecResult.Error(errors.ToString(), ExitCodes.Failure);
    }
}
