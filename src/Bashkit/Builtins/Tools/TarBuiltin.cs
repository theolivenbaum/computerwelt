using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Bashkit.Builtins;

/// <summary>
/// <c>tar</c> — create, list and extract archives inside the virtual filesystem.
/// </summary>
/// <remarks>
/// <para>
/// A real ustar archive is written, byte for byte, so an archive made here can be read by
/// any tar and one made elsewhere can be read here. Nothing about it is a stand-in: the
/// 512-byte header, the octal fields and the checksum are all as the format specifies.
/// </para>
/// <para>
/// Paths are stored exactly as given, absolute ones included. That differs from GNU tar,
/// which strips the leading slash — but here there is no host filesystem to protect, and
/// keeping the path means an extract restores what a create captured.
/// </para>
/// </remarks>
public sealed class TarBuiltin : IBuiltin
{
    private const int BlockSize = 512;

    /// <inheritdoc />
    public string Name => "tar";

    /// <inheritdoc />
    public string? LlmHint =>
        "tar: Create (-c), list (-t) and extract (-x) archives, with -z for gzip, -f FILE, "
        + "-O to extract to stdout, -v for verbose.";

    /// <inheritdoc />
    public string? Help =>
        """
        Usage: tar [OPTION]... [FILE]...
        Create, list and extract archives.

          -c        create an archive
          -t        list an archive's contents
          -x        extract an archive
          -f FILE   use FILE as the archive
          -z        filter the archive through gzip
          -v        list files as they are processed
          -O        extract to standard output
          -C DIR    change to DIR before extracting
        """;

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var mode = '\0';
        var gzip = false;
        var verbose = false;
        var toStdout = false;
        string? archive = null;
        string? directory = null;
        var operands = new List<string>();

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            var argument = context.Arguments[i];

            switch (argument)
            {
                case "--help":
                    return ExecResult.Ok(Help + "\n");

                case "--create": mode = 'c'; continue;
                case "--list": mode = 't'; continue;
                case "--extract": mode = 'x'; continue;
                case "--gzip": gzip = true; continue;
                case "--verbose": verbose = true; continue;
                case "--to-stdout": toStdout = true; continue;

                case "--file" when i + 1 < context.Arguments.Count:
                    archive = context.Arguments[++i];
                    continue;
            }

            if (argument.Length > 1 && argument[0] == '-' && !argument.StartsWith("--", StringComparison.Ordinal))
            {
                for (var c = 1; c < argument.Length; c++)
                {
                    switch (argument[c])
                    {
                        case 'c' or 't' or 'x': mode = argument[c]; break;
                        case 'z' or 'j' or 'J': gzip = true; break;
                        case 'v': verbose = true; break;
                        case 'O': toStdout = true; break;
                        case 'p' or 'm' or 'k': break;

                        case 'f':
                            archive = c + 1 < argument.Length ? argument[(c + 1)..]
                                : i + 1 < context.Arguments.Count ? context.Arguments[++i] : null;

                            c = argument.Length;
                            break;

                        case 'C':
                            directory = c + 1 < argument.Length ? argument[(c + 1)..]
                                : i + 1 < context.Arguments.Count ? context.Arguments[++i] : null;

                            c = argument.Length;
                            break;

                        default:
                            return ExecResult.Usage(Name, $"invalid option -- '{argument[c]}'");
                    }
                }

                continue;
            }

            operands.Add(argument);
        }

        if (mode == '\0')
        {
            return ExecResult.Usage(Name, "You must specify one of the -ctx options");
        }

        return mode switch
        {
            'c' => await CreateAsync(context, archive, gzip, verbose, operands, cancellationToken),
            't' => await ListAsync(context, archive, gzip, cancellationToken),
            _ => await ExtractAsync(context, archive, gzip, verbose, toStdout, directory, cancellationToken),
        };
    }

    /// <summary>One archive member.</summary>
    /// <param name="Path">The stored path.</param>
    /// <param name="Content">Its bytes; empty for a directory.</param>
    /// <param name="IsDirectory">Whether the member is a directory.</param>
    /// <param name="Mode">The permission bits.</param>
    private sealed record Member(string Path, byte[] Content, bool IsDirectory, int Mode);

    private async ValueTask<ExecResult> CreateAsync(
        BuiltinContext context,
        string? archive,
        bool gzip,
        bool verbose,
        List<string> operands,
        CancellationToken cancellationToken)
    {
        if (operands.Count == 0)
        {
            return ExecResult.Usage(Name, "Cowardly refusing to create an empty archive");
        }

        var members = new List<Member>();
        var log = new StringBuilder();

        foreach (var operand in operands)
        {
            if (!await CollectAsync(context, context.ResolvePath(operand), operand, members, cancellationToken))
            {
                return ExecResult.Error(
                    $"tar: {operand}: Cannot stat: No such file or directory\ntar: Exiting with failure status\n",
                    ExitCodes.Usage);
            }
        }

        foreach (var member in members)
        {
            if (verbose)
            {
                log.Append(member.Path).Append('\n');
            }
        }

        var bytes = Write(members);
        context.Budget.ChargeWork(bytes.Length);

        if (gzip)
        {
            bytes = Compress(bytes);
        }

        if (archive is null or "-")
        {
            return ExecResult.Ok(Encoding.Latin1.GetString(bytes));
        }

        await context.FileSystem.WriteFileAsync(context.ResolvePath(archive), bytes, cancellationToken);
        return ExecResult.Ok(log.ToString());
    }

    private static async ValueTask<bool> CollectAsync(
        BuiltinContext context,
        VPath path,
        string display,
        List<Member> members,
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
            return false;
        }

        if (!metadata.IsDirectory)
        {
            members.Add(new Member(
                display,
                await context.FileSystem.ReadFileAsync(path, cancellationToken),
                IsDirectory: false,
                metadata.Mode));

            return true;
        }

        // A directory member's stored name ends in a slash, which is how tar marks one.
        members.Add(new Member(display.TrimEnd('/') + "/", [], IsDirectory: true, metadata.Mode));

        foreach (var entry in (await context.FileSystem.ReadDirectoryAsync(path, cancellationToken))
                     .OrderBy(static e => e.Name, StringComparer.Ordinal))
        {
            await CollectAsync(
                context,
                path.Join(entry.Name),
                display.TrimEnd('/') + "/" + entry.Name,
                members,
                cancellationToken);
        }

        return true;
    }

    private async ValueTask<ExecResult> ListAsync(
        BuiltinContext context,
        string? archive,
        bool gzip,
        CancellationToken cancellationToken)
    {
        var members = await ReadArchiveAsync(context, archive, gzip, cancellationToken);

        if (members is null)
        {
            return ExecResult.Error($"tar: {archive}: Cannot open: No such file or directory\n", ExitCodes.Usage);
        }

        var output = new StringBuilder();

        foreach (var member in members)
        {
            output.Append(member.Path).Append('\n');
        }

        return ExecResult.Ok(output.ToString());
    }

    private async ValueTask<ExecResult> ExtractAsync(
        BuiltinContext context,
        string? archive,
        bool gzip,
        bool verbose,
        bool toStdout,
        string? directory,
        CancellationToken cancellationToken)
    {
        var members = await ReadArchiveAsync(context, archive, gzip, cancellationToken);

        if (members is null)
        {
            return ExecResult.Error($"tar: {archive}: Cannot open: No such file or directory\n", ExitCodes.Usage);
        }

        var output = new StringBuilder();
        var root = directory is null ? context.WorkingDirectory : context.ResolvePath(directory);

        foreach (var member in members)
        {
            context.Budget.ChargeWork(1);

            if (toStdout)
            {
                if (!member.IsDirectory)
                {
                    output.Append(Encoding.UTF8.GetString(member.Content));
                }

                continue;
            }

            if (verbose)
            {
                output.Append(member.Path).Append('\n');
            }

            var target = member.Path.StartsWith('/')
                ? VPath.Parse(member.Path)
                : VPath.Resolve(root, member.Path);

            if (member.IsDirectory)
            {
                await context.FileSystem.CreateDirectoryAsync(target, recursive: true, cancellationToken);
                continue;
            }

            if (target.Parent is { } parent)
            {
                await context.FileSystem.CreateDirectoryAsync(parent, recursive: true, cancellationToken);
            }

            await context.FileSystem.WriteFileAsync(target, member.Content, cancellationToken);
        }

        return ExecResult.Ok(output.ToString());
    }

    private static async ValueTask<List<Member>?> ReadArchiveAsync(
        BuiltinContext context,
        string? archive,
        bool gzip,
        CancellationToken cancellationToken)
    {
        byte[] bytes;

        if (archive is null or "-")
        {
            bytes = Encoding.Latin1.GetBytes(context.StdinText);
        }
        else
        {
            try
            {
                bytes = await context.FileSystem.ReadFileAsync(context.ResolvePath(archive), cancellationToken);
            }
            catch (BashkitException)
            {
                return null;
            }
        }

        context.Budget.ChargeWork(bytes.Length);

        // A gzip archive is recognised by its magic bytes, so `-z` is a hint rather than a
        // requirement — which is what makes `tar -tf x.tar.gz` work.
        if (gzip || (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b))
        {
            bytes = Decompress(bytes);
        }

        return Read(bytes);
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var buffer = new MemoryStream();

        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }

        return buffer.ToArray();
    }

    private static byte[] Decompress(byte[] bytes)
    {
        using var source = new MemoryStream(bytes);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        gzip.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Writes members as a ustar archive, closing with the two empty blocks.</summary>
    private static byte[] Write(List<Member> members)
    {
        using var buffer = new MemoryStream();

        foreach (var member in members)
        {
            buffer.Write(Header(member));

            if (member.Content.Length == 0)
            {
                continue;
            }

            buffer.Write(member.Content);
            var padding = (BlockSize - (member.Content.Length % BlockSize)) % BlockSize;
            buffer.Write(new byte[padding]);
        }

        buffer.Write(new byte[BlockSize * 2]);
        return buffer.ToArray();
    }

    private static byte[] Header(Member member)
    {
        var header = new byte[BlockSize];

        void Put(int offset, int length, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Array.Copy(bytes, 0, header, offset, Math.Min(bytes.Length, length - 1));
        }

        // A long path is split across the `prefix` field, which is what ustar adds over the
        // original format.
        var path = member.Path;
        var prefix = string.Empty;

        if (path.Length > 99)
        {
            var split = path.LastIndexOf('/', Math.Min(path.Length - 1, 154));
            prefix = split > 0 ? path[..split] : string.Empty;
            path = split > 0 ? path[(split + 1)..] : path[..99];
        }

        Put(0, 100, path);
        Put(100, 8, Convert.ToString(member.Mode & 0xFFF, 8).PadLeft(7, '0'));
        Put(108, 8, "0000000");
        Put(116, 8, "0000000");
        Put(124, 12, Convert.ToString(member.Content.Length, 8).PadLeft(11, '0'));
        Put(136, 12, Convert.ToString(0, 8).PadLeft(11, '0'));
        Put(156, 2, member.IsDirectory ? "5" : "0");
        Put(257, 6, "ustar");
        Put(263, 3, "00");
        Put(345, 155, prefix);

        // The checksum is computed with its own field read as blanks, then written in.
        for (var i = 148; i < 156; i++)
        {
            header[i] = (byte)' ';
        }

        var sum = header.Aggregate(0, static (total, b) => total + b);
        Put(148, 8, Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ");
        return header;
    }

    private static List<Member> Read(byte[] bytes)
    {
        var members = new List<Member>();
        var offset = 0;

        while (offset + BlockSize <= bytes.Length)
        {
            var name = Text(bytes, offset, 100);

            // Two consecutive empty blocks end the archive; one empty name is enough here.
            if (name.Length == 0)
            {
                break;
            }

            var prefix = Text(bytes, offset + 345, 155);
            var path = prefix.Length > 0 ? prefix + "/" + name : name;
            var size = (int)Octal(bytes, offset + 124, 12);
            var type = (char)bytes[offset + 156];
            var mode = (int)Octal(bytes, offset + 100, 8);
            offset += BlockSize;

            var content = new byte[Math.Max(size, 0)];

            if (size > 0 && offset + size <= bytes.Length)
            {
                Array.Copy(bytes, offset, content, 0, size);
            }

            offset += (size + BlockSize - 1) / BlockSize * BlockSize;
            members.Add(new Member(path, content, type == '5' || path.EndsWith('/'), mode == 0 ? 0b110_100_100 : mode));
        }

        return members;
    }

    private static string Text(byte[] bytes, int offset, int length)
    {
        var end = offset;

        while (end < offset + length && end < bytes.Length && bytes[end] != 0)
        {
            end++;
        }

        return Encoding.UTF8.GetString(bytes, offset, end - offset);
    }

    private static long Octal(byte[] bytes, int offset, int length)
    {
        var text = Text(bytes, offset, length).Trim();
        return text.Length == 0 ? 0 : Convert.ToInt64(text, 8);
    }
}
