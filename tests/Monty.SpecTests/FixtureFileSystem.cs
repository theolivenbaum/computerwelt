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
    public byte[] Read(string path) =>
        _files.TryGetValue(Absolute(path), out var content)
            ? content
            : throw new PyRaise(new PyException(
                PyExceptionType.FileNotFoundError,
                $"[Errno 2] No such file or directory: '{path}'"));

    /// <inheritdoc />
    public void Write(string path, byte[] content) => _files[Absolute(path)] = content;

    /// <inheritdoc />
    public void Append(string path, byte[] content)
    {
        var full = Absolute(path);
        _files[full] = _files.TryGetValue(full, out var existing) ? [.. existing, .. content] : content;
    }

    /// <inheritdoc />
    public void Remove(string path)
    {
        if (!_files.Remove(Absolute(path)))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.FileNotFoundError,
                $"[Errno 2] No such file or directory: '{path}'"));
        }
    }

    /// <inheritdoc />
    public void CreateDirectory(string path, bool parents, bool existsOk)
    {
        var full = Absolute(path);

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

        for (var current = full; current != "/"; current = Parent(current))
        {
            _directories.Add(current);
        }
    }

    /// <inheritdoc />
    public void RemoveDirectory(string path) => _directories.Remove(Absolute(path));

    /// <inheritdoc />
    public IReadOnlyList<string> List(string path)
    {
        var full = Absolute(path).TrimEnd('/');

        if (!_directories.Contains(full.Length == 0 ? "/" : full))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.FileNotFoundError,
                $"[Errno 2] No such file or directory: '{path}'"));
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
        var source = Absolute(from);

        if (!_files.TryGetValue(source, out var content))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.FileNotFoundError,
                $"[Errno 2] No such file or directory: '{from}'"));
        }

        _files.Remove(source);
        _files[Absolute(to)] = content;
    }

    /// <inheritdoc />
    public int Mode(string path)
    {
        var full = Absolute(path);

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
