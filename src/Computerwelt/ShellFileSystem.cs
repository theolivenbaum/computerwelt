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
    public bool Exists(string path) => Run(path, () => _fileSystem.ExistsAsync(Resolve(path)));

    /// <inheritdoc />
    public bool IsFile(string path) => Stat(path)?.IsFile ?? false;

    /// <inheritdoc />
    public bool IsDirectory(string path) => Stat(path)?.IsDirectory ?? false;

    /// <inheritdoc />
    public byte[] Read(string path)
    {
        try
        {
            return Run(path, () => _fileSystem.ReadFileAsync(Resolve(path)));
        }
        catch (PyRaise)
        {
            throw NotFound(path);
        }
    }

    /// <inheritdoc />
    public void Write(string path, byte[] content) =>
        Run(path, () => _fileSystem.WriteFileAsync(Resolve(path), content));

    /// <inheritdoc />
    public void Append(string path, byte[] content) =>
        Run(path, () => _fileSystem.AppendFileAsync(Resolve(path), content));

    /// <inheritdoc />
    public void Remove(string path)
    {
        try
        {
            Run(path, () => _fileSystem.RemoveAsync(Resolve(path), recursive: false));
        }
        catch (PyRaise)
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

        Run(path, () => _fileSystem.CreateDirectoryAsync(Resolve(path), parents));
    }

    /// <inheritdoc />
    public void RemoveDirectory(string path)
    {
        try
        {
            Run(path, () => _fileSystem.RemoveAsync(Resolve(path), recursive: false));
        }
        catch (PyRaise)
        {
            throw NotFound(path);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> List(string path) =>
        [.. Run(path, () => _fileSystem.ReadDirectoryAsync(Resolve(path))).Select(static e => e.Name)];

    /// <inheritdoc />
    public long Size(string path) => Stat(path)?.Size ?? throw NotFound(path);

    /// <inheritdoc />
    public void Rename(string from, string to)
    {
        try
        {
            Run(from, () => _fileSystem.RenameAsync(Resolve(from), Resolve(to)));
        }
        catch (PyRaise)
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
            return Run(path, () => _fileSystem.StatAsync(Resolve(path)));
        }
        catch (PyRaise)
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
    private static T Run<T>(string path, Func<ValueTask<T>> operation)
    {
        try
        {
            var task = operation();
            return task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
        }
        catch (ShellException e)
        {
            throw Translate(e, path);
        }
    }

    private static void Run(string path, Func<ValueTask> operation)
    {
        try
        {
            var task = operation();

            if (!task.IsCompletedSuccessfully)
            {
                task.AsTask().GetAwaiter().GetResult();
            }
        }
        catch (ShellException e)
        {
            throw Translate(e, path);
        }
    }

    /// <summary>
    /// Turns a shell filesystem failure into the Python exception CPython would raise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every call into the shell's filesystem goes through <c>Run</c>, so this is the one
    /// place a failure can cross the boundary — and it must, because a
    /// <see cref="ShellException"/> escaping into a Python program is a host exception
    /// leaving the sandbox, not an error the program can catch. Before this existed,
    /// <c>os.makedirs</c> past the depth limit brought the whole call down.
    /// </para>
    /// <para>
    /// The kind, not the message, decides which exception: the shell's wording is what a
    /// POSIX shell prints and Python's is <c>[Errno N] …: 'path'</c>, and both are pinned
    /// by their own corpus.
    /// </para>
    /// </remarks>
    private static PyRaise Translate(ShellException error, string path)
    {
        var kind = (error as FileSystemException)?.FsKind;

        return kind switch
        {
            FileSystemErrorKind.NotFound => NotFound(path),
            FileSystemErrorKind.NotADirectory => NotADirectory(path),
            FileSystemErrorKind.AlreadyExists => Raise(
                PyExceptionType.FileExistsError, 17, "File exists", path),
            FileSystemErrorKind.IsADirectory => Raise(
                PyExceptionType.IsADirectoryError, 21, "Is a directory", path),
            FileSystemErrorKind.DirectoryNotEmpty => Raise(
                PyExceptionType.OSError, 39, "Directory not empty", path),
            FileSystemErrorKind.PermissionDenied => Raise(
                PyExceptionType.PermissionError, 13, "Permission denied", path),
            FileSystemErrorKind.TooManySymlinks => Raise(
                PyExceptionType.OSError, 40, "Too many levels of symbolic links", path),
            FileSystemErrorKind.ReadOnly => Raise(
                PyExceptionType.OSError, 30, "Read-only file system", path),
            FileSystemErrorKind.QuotaExceeded => Raise(
                PyExceptionType.OSError, 28, "No space left on device", path),

            // EINVAL covers the shell's own refusals — a name too long, nesting too deep —
            // whose detail is the only description of what went wrong, so it is kept.
            FileSystemErrorKind.InvalidArgument => Raise(
                PyExceptionType.OSError, 22, Detail(error), path),
            _ => Raise(PyExceptionType.OSError, 0, Detail(error), path),
        };
    }

    /// <summary>The part of a shell filesystem message after the path it names.</summary>
    private static string Detail(ShellException error)
    {
        var colon = error.Message.LastIndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? error.Message : error.Message[(colon + 2)..];
    }

    private static PyRaise Raise(PyExceptionType type, int errno, string detail, string path) =>
        new(new PyException(type, $"[Errno {errno}] {detail}: '{path}'"));

    /// <summary>Reads a file as text, for hosts that want the shell's view of one.</summary>
    public string Text(string path) => Encoding.UTF8.GetString(Read(path));
}
