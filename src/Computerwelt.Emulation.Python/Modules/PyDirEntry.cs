using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>One entry from <c>os.scandir</c>.</summary>
/// <remarks>
/// The point of <c>scandir</c> over <c>listdir</c> is that the listing already knows what
/// each entry is, so a walk does not pay a separate query per name. Here the saving is the
/// same in shape if not in kind: the type is read once when the entry is built and answered
/// from the cache afterwards, exactly as CPython's <c>DirEntry</c> caches its <c>stat</c>.
/// </remarks>
public sealed class PyDirEntry : PyObject
{
    private readonly IPyFileSystem _fileSystem;
    private readonly bool _isDirectory;

    /// <summary>Creates an entry.</summary>
    /// <param name="name">The entry's name within its directory.</param>
    /// <param name="path">The path the listing was made from, joined with the name.</param>
    /// <param name="fileSystem">Where it lives.</param>
    public PyDirEntry(string name, string path, IPyFileSystem fileSystem)
    {
        Name = name;
        Path = path;
        _fileSystem = fileSystem;
        _isDirectory = fileSystem.IsDirectory(path);
    }

    /// <summary>The entry's name.</summary>
    public string Name { get; }

    /// <summary>The entry's path, as the listing would have spelled it.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public override string TypeName => "posix.DirEntry";

    /// <inheritdoc />
    public override string Repr() => $"<DirEntry '{Name}'>";

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "name" => new PyStr(Name),
        "path" => new PyStr(Path),

        // Cached from the listing rather than re-queried, which is the whole point.
        "is_dir" => new PyBuiltinFunction("is_dir", _ => PyBool.Of(_isDirectory)),
        "is_file" => new PyBuiltinFunction("is_file", _ => PyBool.Of(!_isDirectory && _fileSystem.Exists(Path))),

        // No filesystem here has symbolic links, so this is a constant rather than a query.
        "is_symlink" => new PyBuiltinFunction("is_symlink", static _ => PyBool.False),

        "stat" => new PyBuiltinFunction("stat", _ => new PyStat(
            _fileSystem.Mode(Path),
            _isDirectory ? 0 : _fileSystem.Size(Path),
            _fileSystem.ModifiedAt(Path))),

        // `inode` has to return something an entry can be identified by; the path is the
        // only stable identity a virtual filesystem has, so its hash is what is reported.
        "inode" => new PyBuiltinFunction("inode", _ => new PyInt(
            (uint)System.StringComparer.Ordinal.GetHashCode(Path))),

        "__fspath__" => new PyBuiltinFunction("__fspath__", _ => new PyStr(Path)),
        _ => null,
    };
}

/// <summary>What <c>os.scandir</c> returns: an iterator that is also a context manager.</summary>
/// <remarks>
/// The listing is taken up front rather than streamed. CPython holds an open directory
/// handle, which is why its result must be closed; there is no handle here, so
/// <c>close()</c> and <c>__exit__</c> exist to keep <c>with os.scandir(…) as it:</c>
/// working rather than to release anything.
/// </remarks>
public sealed class PyScandir : PyObject
{
    private readonly List<PyObject> _entries;
    private int _position;

    /// <summary>Creates the result of scanning <paramref name="path"/>.</summary>
    public PyScandir(string path, IEnumerable<PyDirEntry> entries)
    {
        Path = path;
        _entries = [.. entries];
    }

    /// <summary>The directory that was scanned.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public override string TypeName => "posix.ScandirIterator";

    /// <inheritdoc />
    public override string Repr() => $"<posix.ScandirIterator object>";

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate()
    {
        while (_position < _entries.Count)
        {
            yield return _entries[_position++];
        }
    }

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "close" => new PyBuiltinFunction("close", _ =>
        {
            _position = _entries.Count;
            return PyNone.Instance;
        }),

        "__enter__" => new PyBuiltinFunction("__enter__", _ => this),

        "__exit__" => new PyBuiltinFunction("__exit__", _ =>
        {
            _position = _entries.Count;
            return PyNone.Instance;
        }),

        _ => null,
    };
}
