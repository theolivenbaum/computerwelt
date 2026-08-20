using System.Text;
using Monty.Runtime;

namespace Monty.SpecTests;

/// <summary>
/// The virtual filesystem upstream's harness exposes to the fixtures.
/// </summary>
/// <remarks>
/// Mirrors the tree in <c>crates/monty-datatest/src/main.rs</c>, because the fixtures
/// assert on its exact contents. It is test scaffolding: the library itself hands a program
/// no filesystem at all.
/// </remarks>
public sealed class FixtureFileSystem : IPyFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal)
    {
        ["/virtual/file.txt"] = "hello world\n"u8.ToArray(),
        ["/virtual/data.bin"] = [0x00, 0x01, 0x02, 0x03],
        ["/virtual/empty.txt"] = [],
        ["/virtual/subdir/nested.txt"] = "nested content"u8.ToArray(),
        ["/virtual/subdir/deep/file.txt"] = "deep"u8.ToArray(),
        ["/virtual/readonly.txt"] = "readonly"u8.ToArray(),

        // The tree a `# mount-fs` fixture sees under `root`.
        ["/mnt/hello.txt"] = "hello world\n"u8.ToArray(),
        ["/mnt/empty.txt"] = [],
        ["/mnt/data.bin"] = [0x00, 0x01, 0x02, 0x03],
        ["/mnt/subdir/nested.txt"] = "nested content"u8.ToArray(),
        ["/mnt/subdir/deep/file.txt"] = "deep file"u8.ToArray(),
        ["/mnt/readonly.txt"] = "readonly content"u8.ToArray(),
    };

    private readonly HashSet<string> _directories = new(StringComparer.Ordinal)
    {
        "/", "/virtual", "/virtual/subdir", "/virtual/subdir/deep",
        "/mnt", "/mnt/subdir", "/mnt/subdir/deep",
    };

    /// <inheritdoc />
    public string WorkingDirectory => "/virtual";

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Environment { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["VIRTUAL_HOME"] = "/virtual/home",
        ["VIRTUAL_USER"] = "testuser",
        ["VIRTUAL_EMPTY"] = string.Empty,
    };

    /// <inheritdoc />
    public bool Exists(string path) => IsFile(path) || IsDirectory(path);

    /// <inheritdoc />
    public bool IsFile(string path) => _files.ContainsKey(Absolute(path));

    /// <inheritdoc />
    public bool IsDirectory(string path) => _directories.Contains(Absolute(path));

    /// <inheritdoc />
    public byte[] Read(string path)
    {
        var full = Reachable(path);

        if (_files.TryGetValue(full, out var content))
        {
            return content;
        }

        throw new PyRaise(new PyException(
            _directories.Contains(full) ? PyExceptionType.IsADirectoryError : PyExceptionType.FileNotFoundError,
            _directories.Contains(full)
                ? $"[Errno 21] Is a directory: '{path}'"
                : $"[Errno 2] No such file or directory: '{path}'"));
    }

    /// <inheritdoc />
    public void Write(string path, byte[] content) => _files[Writable(path)] = content;

    /// <inheritdoc />
    public void Append(string path, byte[] content)
    {
        var full = Writable(path);
        _files[full] = _files.TryGetValue(full, out var existing) ? [.. existing, .. content] : content;
    }

    /// <summary>Resolves a path to write to, refusing one that cannot hold a file.</summary>
    private string Writable(string path)
    {
        var full = Reachable(path);

        if (_directories.Contains(full))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.IsADirectoryError,
                $"[Errno 21] Is a directory: '{path}'"));
        }

        if (!_directories.Contains(Parent(full)))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.FileNotFoundError,
                $"[Errno 2] No such file or directory: '{path}'"));
        }

        return full;
    }

    /// <inheritdoc />
    public void Remove(string path)
    {
        var full = Reachable(path);

        if (_directories.Contains(full))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.IsADirectoryError,
                $"[Errno 21] Is a directory: '{path}'"));
        }

        if (!_files.Remove(full))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.FileNotFoundError,
                $"[Errno 2] No such file or directory: '{path}'"));
        }
    }

    /// <inheritdoc />
    public void CreateDirectory(string path, bool parents, bool existsOk)
    {
        var full = Reachable(path);

        // A file already occupying the name is a FileExistsError whatever `exist_ok` says:
        // the path exists, and it is not the directory that was asked for.
        if (_files.ContainsKey(full))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.FileExistsError,
                $"[Errno 17] File exists: '{path}'"));
        }

        if (_directories.Contains(full))
        {
            if (!existsOk)
            {
                throw new PyRaise(new PyException(
                    PyExceptionType.FileExistsError,
                    $"[Errno 17] File exists: '{path}'"));
            }

            return;
        }

        if (!parents && !_directories.Contains(Parent(full)))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.FileNotFoundError,
                $"[Errno 2] No such file or directory: '{path}'"));
        }

        // A file in the way blocks the whole chain: a directory cannot be created inside
        // one, however many parents were asked for.
        for (var current = full; current != "/"; current = Parent(current))
        {
            if (_files.ContainsKey(current))
            {
                throw new PyRaise(new PyException(
                    PyExceptionType.NotADirectoryError,
                    $"[Errno 20] Not a directory: '{path}'"));
            }
        }

        for (var current = full; current != "/"; current = Parent(current))
        {
            _directories.Add(current);
        }
    }

    /// <inheritdoc />
    public void RemoveDirectory(string path)
    {
        var full = Reachable(path);

        if (_directories.Contains(full) && Occupied(full))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.OSError,
                $"[Errno 39] Directory not empty: '{path}'"));
        }

        // Removing something that is not there is an error, not a no-op — and a file in a
        // directory's place is a different error again.
        if (!_directories.Contains(full))
        {
            throw new PyRaise(new PyException(
                _files.ContainsKey(full) ? PyExceptionType.NotADirectoryError : PyExceptionType.FileNotFoundError,
                _files.ContainsKey(full)
                    ? $"[Errno 20] Not a directory: '{path}'"
                    : $"[Errno 2] No such file or directory: '{path}'"));
        }

        _directories.Remove(full);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> List(string path)
    {
        var full = Absolute(path).TrimEnd('/');

        if (!_directories.Contains(full.Length == 0 ? "/" : full))
        {
            // A path that exists but is a file is a different error from one that is not
            // there at all.
            throw new PyRaise(new PyException(
                _files.ContainsKey(full)
                    ? PyExceptionType.NotADirectoryError
                    : PyExceptionType.FileNotFoundError,
                _files.ContainsKey(full)
                    ? $"[Errno 20] Not a directory: '{path}'"
                    : $"[Errno 2] No such file or directory: '{path}'"));
        }

        var prefix = full + "/";
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in _files.Keys.Concat(_directories))
        {
            if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = candidate[prefix.Length..];
            var slash = rest.IndexOf('/');
            names.Add(slash < 0 ? rest : rest[..slash]);
        }

        return [.. names];
    }

    /// <inheritdoc />
    public long Size(string path) => Read(path).Length;

    /// <inheritdoc />
    public void Rename(string from, string to)
    {
        var source = Reachable(from);
        var target = Reachable(to);

        if (_files.TryGetValue(source, out var content))
        {
            _files.Remove(source);
            _files[target] = content;
            return;
        }

        if (!_directories.Contains(source))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.FileNotFoundError,
                $"[Errno 2] No such file or directory: '{from}'"));
        }

        // A directory may be renamed onto an empty directory, taking its place; onto one
        // that still holds anything it may not.
        if (_directories.Contains(target) && Occupied(target))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.OSError,
                $"[Errno 39] Directory not empty: '{to}'"));
        }

        var prefix = source + "/";

        foreach (var directory in _directories.Where(d => d == source || d.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _directories.Remove(directory);
            _directories.Add(target + directory[source.Length..]);
        }

        foreach (var file in _files.Keys.Where(f => f.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _files[target + file[source.Length..]] = _files[file];
            _files.Remove(file);
        }
    }

    /// <summary>Whether a directory still holds anything.</summary>
    private bool Occupied(string directory)
    {
        var prefix = directory + "/";

        return _files.Keys.Any(f => f.StartsWith(prefix, StringComparison.Ordinal))
            || _directories.Any(d => d.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Resolves a path, refusing one no POSIX filesystem would accept.
    /// </summary>
    /// <remarks>
    /// The limits are the usual ones — 255 bytes per component, 4096 for the whole path —
    /// and they are checked before the tree is, because that is when the kernel checks them.
    /// </remarks>
    private string Reachable(string path)
    {
        var full = Absolute(path);

        var tooLong = Encoding.UTF8.GetByteCount(full) > 4096
            || full.Split('/').Any(static part => Encoding.UTF8.GetByteCount(part) > 255);

        return tooLong
            ? throw new PyRaise(new PyException(
                PyExceptionType.OSError, $"[Errno 36] File name too long: '{path}'"))
            : full;
    }

    /// <inheritdoc />
    public int Mode(string path)
    {
        var full = Reachable(path);

        if (_directories.Contains(full))
        {
            return 0x4000 | Convert.ToInt32("755", 8);
        }

        // The one read-only file in the tree exists so the fixtures can see a mode differ.
        var permissions = full.EndsWith("readonly.txt", StringComparison.Ordinal) ? "444" : "644";
        return 0x8000 | Convert.ToInt32(permissions, 8);
    }

    /// <inheritdoc />
    public double ModifiedAt(string path)
    {
        _ = path;

        // A fixed timestamp keeps the fixtures deterministic.
        return 1_700_000_000.0;
    }

    private string Absolute(string path) =>
        path.StartsWith('/') ? Normalize(path) : Normalize(WorkingDirectory + "/" + path);

    private static string Normalize(string path)
    {
        var parts = new List<string>();

        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case ".":
                    continue;

                case ".." when parts.Count > 0:
                    parts.RemoveAt(parts.Count - 1);
                    continue;

                case "..":
                    continue;

                default:
                    parts.Add(part);
                    continue;
            }
        }

        return "/" + string.Join('/', parts);
    }

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    /// <summary>Renders a file's content as text, for assertions in the harness itself.</summary>
    public string Text(string path) => Encoding.UTF8.GetString(Read(path));
}
