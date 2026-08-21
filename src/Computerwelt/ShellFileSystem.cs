using System.Text;
using Computerwelt.Emulation.Bash;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt;

/// <summary>
/// The shell's virtual filesystem, presented to Python.
/// </summary>
/// <remarks>
/// <para>
/// This adapter is what makes the two halves one sandbox rather than two: a file a shell
/// command wrote is the same file <c>open()</c> reads, because both go through the same
/// <see cref="IFileSystem"/>. Nothing here reaches a host path.
/// </para>
/// <para>
/// The working directory is read through a delegate rather than captured, so
/// <c>os.getcwd()</c> follows a <c>cd</c> that happened earlier in the same script.
/// </para>
/// </remarks>
public sealed class ShellFileSystem : IPyFileSystem
{
    private readonly IFileSystem _fileSystem;
    private readonly Func<VPath> _workingDirectory;
    private readonly Func<IReadOnlyDictionary<string, string>> _environment;

    /// <summary>Creates the adapter.</summary>
    /// <param name="fileSystem">The shell's filesystem.</param>
    /// <param name="workingDirectory">Reads the shell's current directory.</param>
    /// <param name="environment">Reads the shell's exported environment.</param>
    public ShellFileSystem(
        IFileSystem fileSystem,
        Func<VPath> workingDirectory,
        Func<IReadOnlyDictionary<string, string>> environment)
    {
        _fileSystem = fileSystem;
        _workingDirectory = workingDirectory;
        _environment = environment;
    }

    /// <inheritdoc />
    public string WorkingDirectory => _workingDirectory().Value;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Environment => _environment();

    /// <inheritdoc />
    public bool Exists(string path) => Run(() => _fileSystem.ExistsAsync(Resolve(path)));

    /// <inheritdoc />
    public bool IsFile(string path) => Stat(path)?.IsFile ?? false;

    /// <inheritdoc />
    public bool IsDirectory(string path) => Stat(path)?.IsDirectory ?? false;

    /// <inheritdoc />
    public byte[] Read(string path)
    {
        try
        {
            return Run(() => _fileSystem.ReadFileAsync(Resolve(path)));
        }
        catch (ShellException)
        {
            throw NotFound(path);
        }
    }

    /// <inheritdoc />
    public void Write(string path, byte[] content) =>
        Run(() => _fileSystem.WriteFileAsync(Resolve(path), content));

    /// <inheritdoc />
    public void Append(string path, byte[] content) =>
        Run(() => _fileSystem.AppendFileAsync(Resolve(path), content));

    /// <inheritdoc />
    public void Remove(string path)
    {
        try
        {
            Run(() => _fileSystem.RemoveAsync(Resolve(path), recursive: false));
        }
        catch (ShellException)
        {
            throw NotFound(path);
        }
    }

    /// <inheritdoc />
    public void CreateDirectory(string path, bool parents, bool existsOk)
    {
        if (!existsOk && Exists(path))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.FileExistsError,
                $"[Errno 17] File exists: '{path}'"));
        }

        Run(() => _fileSystem.CreateDirectoryAsync(Resolve(path), parents));
    }

    /// <inheritdoc />
    public void RemoveDirectory(string path)
    {
        try
        {
            Run(() => _fileSystem.RemoveAsync(Resolve(path), recursive: false));
        }
        catch (ShellException)
        {
            throw NotFound(path);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> List(string path)
    {
        try
        {
            return [.. Run(() => _fileSystem.ReadDirectoryAsync(Resolve(path))).Select(static e => e.Name)];
        }
        catch (ShellException)
        {
            // Listing something that is there but is not a directory is a different error
            // from listing something that is not there, and scripts branch on which —
            // `tests/monty-spec/mount_fs__ops.py` pins both messages.
            throw Stat(path) is null ? NotFound(path) : NotADirectory(path);
        }
    }

    /// <inheritdoc />
    public long Size(string path) => Stat(path)?.Size ?? throw NotFound(path);

    /// <inheritdoc />
    public void Rename(string from, string to)
    {
        try
        {
            Run(() => _fileSystem.RenameAsync(Resolve(from), Resolve(to)));
        }
        catch (ShellException)
        {
            throw NotFound(from);
        }
    }

    /// <inheritdoc />
    public int Mode(string path)
    {
        var metadata = Stat(path) ?? throw NotFound(path);

        // The type bits POSIX defines, so `st_mode & 0o170000` identifies the kind.
        var type = metadata.Type switch
        {
            FileType.Directory => 0x4000,
            FileType.Symlink => 0xA000,
            FileType.Fifo => 0x1000,
            _ => 0x8000,
        };

        return type | (metadata.Mode & 0xFFF);
    }

    /// <inheritdoc />
    public double ModifiedAt(string path) =>
        (Stat(path) ?? throw NotFound(path)).ModifiedAt.ToUnixTimeMilliseconds() / 1000.0;

    private FileMetadata? Stat(string path)
    {
        try
        {
            return Run(() => _fileSystem.StatAsync(Resolve(path)));
        }
        catch (ShellException)
        {
            return null;
        }
    }

    private VPath Resolve(string path) => VPath.Resolve(_workingDirectory(), path);

    private static PyRaise NotFound(string path) =>
        new(new PyException(
            PyExceptionType.FileNotFoundError,
            $"[Errno 2] No such file or directory: '{path}'"));

    private static PyRaise NotADirectory(string path) =>
        new(new PyException(
            PyExceptionType.NotADirectoryError,
            $"[Errno 20] Not a directory: '{path}'"));

    /// <summary>
    /// Waits for a filesystem call.
    /// </summary>
    /// <remarks>
    /// Python's <c>open</c> and <c>os</c> are synchronous by definition, and threading
    /// asynchrony through the interpreter for them would distort every signature. The
    /// virtual filesystems in play complete synchronously, so the wait is nominal.
    /// </remarks>
    private static T Run<T>(Func<ValueTask<T>> operation)
    {
        var task = operation();
        return task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
    }

    private static void Run(Func<ValueTask> operation)
    {
        var task = operation();

        if (!task.IsCompletedSuccessfully)
        {
            task.AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Reads a file as text, for hosts that want the shell's view of one.</summary>
    public string Text(string path) => Encoding.UTF8.GetString(Read(path));
}
