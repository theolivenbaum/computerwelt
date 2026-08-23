using System.Numerics;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// A <c>pathlib.Path</c>.
/// </summary>
/// <remarks>
/// <para>
/// Paths are POSIX here whatever the host runs on, and the type reports itself as
/// <c>PosixPath</c> to say so. A sandbox that changed its path semantics with the host would
/// make every script that touches a path non-portable.
/// </para>
/// <para>
/// Purely lexical operations — <c>name</c>, <c>parent</c>, <c>/</c>, <c>with_suffix</c> —
/// work with no filesystem at all. The ones that touch storage raise when the host has not
/// supplied one.
/// </para>
/// </remarks>
public sealed class PyPath : PyObject
{
    private readonly IPyFileSystem? _fileSystem;

    /// <summary>Creates a path.</summary>
    /// <param name="value">The path text, already normalised.</param>
    /// <param name="fileSystem">The filesystem its I/O methods use, if any.</param>
    /// <param name="maxDepth">
    /// How deep <c>glob</c> and <c>walk</c> may descend before refusing to go further. It
    /// travels with the path so that a <c>Path</c> produced by a walk carries the same cap
    /// as the one the walk started from.
    /// </param>
    public PyPath(string value, IPyFileSystem? fileSystem = null, int maxDepth = DefaultMaxDepth)
    {
        Value = value;
        _fileSystem = fileSystem;
        MaxDepth = maxDepth;
    }

    /// <summary>The depth cap used when no host set one — the shell filesystem's own.</summary>
    internal const int DefaultMaxDepth = 64;

    /// <summary>How deep this path's traversals may descend.</summary>
    public int MaxDepth { get; }

    /// <summary>The path text.</summary>
    public string Value { get; }

    /// <summary>The filesystem this path's I/O methods use, if the host supplied one.</summary>
    public IPyFileSystem? FileSystem => _fileSystem;

    /// <inheritdoc />
    public override string TypeName => "PosixPath";

    /// <inheritdoc />
    public override string Repr() => $"PosixPath({PyStr.Quote(Value)})";

    /// <inheritdoc />
    public override string Display() => Value;

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) =>
        other is PyPath path && string.Equals(Value, path.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override BigInteger PyHash() => new(Value.GetHashCode(StringComparison.Ordinal));

    /// <inheritdoc />
    public override int? PyCompare(PyObject other) =>
        other is PyPath path ? string.CompareOrdinal(Value, path.Value) : null;

    /// <summary>
    /// Joins path segments the way <c>Path(...)</c> and <c>/</c> do: an absolute segment
    /// discards everything before it.
    /// </summary>
    public static string Join(string left, string right)
    {
        if (right.StartsWith('/'))
        {
            return Normalize(right);
        }

        if (right.Length == 0 || right == ".")
        {
            return left;
        }

        if (left.Length == 0 || left == ".")
        {
            return Normalize(right);
        }

        return Normalize(left.TrimEnd('/') + "/" + right);
    }

    /// <summary>
    /// Collapses redundant separators and <c>.</c> components, as <c>PurePath</c> does.
    /// </summary>
    /// <remarks>
    /// <c>..</c> is deliberately kept: removing it would change which file the path names
    /// whenever a symlink is involved, which is why CPython leaves it for <c>resolve()</c>.
    /// </remarks>
    public static string Normalize(string path)
    {
        if (path.Length == 0)
        {
            return ".";
        }

        var absolute = path.StartsWith('/');
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(static part => part != ".")
            .ToList();

        var joined = string.Join('/', parts);

        if (absolute)
        {
            return "/" + joined;
        }

        return joined.Length == 0 ? "." : joined;
    }

    /// <summary>The final component, or an empty string for the root.</summary>
    public string Name
    {
        get
        {
            var trimmed = Value.TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            var name = slash < 0 ? trimmed : trimmed[(slash + 1)..];
            return name == "." ? string.Empty : name;
        }
    }

    /// <summary>The containing directory.</summary>
    public string Parent
    {
        get
        {
            if (Value == "/" || Value == ".")
            {
                return Value;
            }

            var trimmed = Value.TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');

            return slash switch
            {
                < 0 => ".",
                0 => "/",
                _ => trimmed[..slash],
            };
        }
    }

    /// <summary>The final component without its last suffix.</summary>
    public string Stem
    {
        get
        {
            var name = Name;
            var dot = name.LastIndexOf('.');
            return dot > 0 ? name[..dot] : name;
        }
    }

    /// <summary>The last suffix, including the dot, or an empty string.</summary>
    public string Suffix
    {
        get
        {
            var name = Name;
            var dot = name.LastIndexOf('.');
            return dot > 0 ? name[dot..] : string.Empty;
        }
    }

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "name" => new PyStr(Name),
        "parent" => new PyPath(Parent, _fileSystem, MaxDepth),
        "stem" => new PyStr(Stem),
        "suffix" => new PyStr(Suffix),
        "suffixes" => new PyList([.. Suffixes().Select(static s => (PyObject)new PyStr(s))]),
        "parts" => new PyTuple([.. Parts().Select(static p => (PyObject)new PyStr(p))]),
        "parents" => new PyList([.. Ancestors().Select(p => (PyObject)new PyPath(p, _fileSystem, MaxDepth))]),
        _ => Method(name),
    };

    private IEnumerable<string> Suffixes()
    {
        var name = Name;
        var parts = name.TrimStart('.').Split('.');

        for (var i = 1; i < parts.Length; i++)
        {
            yield return "." + parts[i];
        }
    }

    private IEnumerable<string> Parts()
    {
        if (Value == ".")
        {
            yield break;
        }

        if (Value.StartsWith('/'))
        {
            yield return "/";
        }

        foreach (var part in Value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return part;
        }
    }

    private IEnumerable<string> Ancestors()
    {
        var current = Parent;

        while (true)
        {
            yield return current;

            if (current is "/" or ".")
            {
                yield break;
            }

            current = new PyPath(current).Parent;
        }
    }

    private PyObject? Method(string name)
    {
        switch (name)
        {
            case "joinpath":
                return Builtin(name, arguments =>
                    new PyPath(arguments.Aggregate(Value, (path, part) => Join(path, Text(part))), _fileSystem, MaxDepth));

            case "with_name":
                return Builtin(name, arguments => new PyPath(Join(Parent, Text(arguments[0])), _fileSystem, MaxDepth));

            case "with_suffix":
                return Builtin(name, arguments =>
                    new PyPath(Join(Parent, Stem + Text(arguments[0])), _fileSystem, MaxDepth));

            case "is_absolute":
                return Builtin(name, _ => PyBool.Of(Value.StartsWith('/')));

            case "as_posix" or "__fspath__":
                return Builtin(name, _ => new PyStr(Value));

            case "exists":
                return Builtin(name, _ => PyBool.Of(Storage().Exists(Value)));

            case "is_file":
                return Builtin(name, _ => PyBool.Of(Storage().IsFile(Value)));

            case "is_dir":
                return Builtin(name, _ => PyBool.Of(Storage().IsDirectory(Value)));

            // There are no symlinks in a virtual tree, so this is always false rather than
            // unimplemented — scripts branch on it.
            case "is_symlink":
                return Builtin(name, _ => PyBool.False);

            case "stat" or "lstat":
                return Builtin(name, _ => new PyStat(
                    Storage().Mode(Value),
                    Storage().IsDirectory(Value) ? 0 : Storage().Size(Value),
                    Storage().ModifiedAt(Value)));

            case "touch":
                return Builtin(name, _ =>
                {
                    if (!Storage().Exists(Value))
                    {
                        Storage().Write(Value, []);
                    }

                    return PyNone.Instance;
                });

            case "samefile":
                return Builtin(name, arguments => PyBool.Of(
                    string.Equals(Value, Text(arguments[0]), StringComparison.Ordinal)));

            // The same call as the `open` builtin, with this path bound as its `file`, so
            // the mode parsing and the rejections stay in one place.
            case "open":
                return new PyBuiltinFunction("open", (arguments, keywords) =>
                    OsModule.Open(Storage(), [this, .. arguments], keywords));

            // The decode is strict, as `read_text` is: a file that is not UTF-8 raises
            // rather than coming back with replacement characters in it.
            case "read_text":
                return Builtin(name, _ => new PyStr(Codecs.Decode(Storage().Read(Value), "utf-8", "strict")));

            case "read_bytes":
                return Builtin(name, _ => new PyBytes(Storage().Read(Value)));

            case "write_text":
                return Builtin(name, arguments =>
                {
                    if (arguments.Length == 0)
                    {
                        throw new PyRaise(PyErrors.TypeError(
                            "Path.write_text() missing 1 required positional argument: 'data'"));
                    }

                    if (arguments[0] is not PyStr)
                    {
                        throw new PyRaise(PyErrors.TypeError(
                            $"data must be str, not {arguments[0].TypeName}"));
                    }

                    // The count is of characters written, not of the bytes they encode to.
                    var text = Text(arguments[0]);
                    Storage().Write(Value, System.Text.Encoding.UTF8.GetBytes(text));

                    return PyInt.From(new PyStr(text).Length() ?? text.Length);
                });

            case "write_bytes":
                return Builtin(name, arguments =>
                {
                    if (arguments.Length == 0)
                    {
                        throw new PyRaise(PyErrors.TypeError(
                            "Path.write_bytes() missing 1 required positional argument: 'data'"));
                    }

                    // The argument goes through a memoryview upstream, which is what names
                    // itself when the value is not bytes-like.
                    var bytes = arguments[0] is PyBytes payload
                        ? payload.Value
                        : throw new PyRaise(PyErrors.TypeError(
                            $"memoryview: a bytes-like object is required, not '{arguments[0].TypeName}'"));
                    Storage().Write(Value, bytes);
                    return PyInt.From(bytes.Length);
                });

            case "unlink":
                return Builtin(name, _ =>
                {
                    Storage().Remove(Value);
                    return PyNone.Instance;
                });

            case "mkdir":
                // `parents` and `exist_ok` are read for truth, so an empty string does not
                // enable either of them.
                return new PyBuiltinFunction(name, (arguments, keywords) =>
                {
                    if (arguments.Length > 3)
                    {
                        throw new PyRaise(PyErrors.TypeError(
                            "Path.mkdir() takes from 0 to 3 positional arguments "
                            + $"but {arguments.Length} were given"));
                    }

                    foreach (var (key, _) in keywords?.Entries ?? [])
                    {
                        if (key.Display() is not ("mode" or "parents" or "exist_ok"))
                        {
                            throw new PyRaise(PyErrors.TypeError(
                                $"Path.mkdir() got an unexpected keyword argument '{key.Display()}'"));
                        }

                        var position = key.Display() switch
                        {
                            "mode" => 0,
                            "parents" => 1,
                            _ => 2,
                        };

                        if (arguments.Length > position)
                        {
                            throw new PyRaise(PyErrors.TypeError(
                                $"Path.mkdir() got multiple values for argument '{key.Display()}'"));
                        }
                    }

                    Storage().CreateDirectory(
                        Value,
                        Flag(arguments, keywords, 1, "parents"),
                        Flag(arguments, keywords, 2, "exist_ok"));

                    return PyNone.Instance;
                });

            case "rmdir":
                return Builtin(name, _ =>
                {
                    Storage().RemoveDirectory(Value);
                    return PyNone.Instance;
                });

            case "iterdir":
                return Builtin(name, _ => new PyList(
                    [.. Storage().List(Value).OrderBy(static e => e, StringComparer.Ordinal)
                        .Select(entry => (PyObject)new PyPath(Join(Value, entry), _fileSystem, MaxDepth))]));

            case "rename":
                return Builtin(name, arguments =>
                {
                    var target = Text(arguments[0]);
                    Storage().Rename(Value, target);
                    return new PyPath(target, _fileSystem, MaxDepth);
                });

            case "glob" or "rglob":
                return new PyBuiltinFunction(name, (arguments, keywords) =>
                {
                    if (arguments.Length == 0)
                    {
                        throw new PyRaise(PyErrors.TypeError(
                            $"Path.{name}() missing 1 required positional argument: 'pattern'"));
                    }

                    var pattern = Text(arguments[0]);

                    // `rglob(p)` is `glob('**/' + p)`, which is also why it needs no
                    // `recursive` flag of its own: the recursion is in the pattern.
                    var expression = name == "rglob" ? "**/" + pattern : pattern;

                    var hidden = keywords?.TryGetValue(new PyStr("include_hidden"), out var flag) == true
                        && flag.IsTruthy();

                    return new PyList([
                        .. Globbing.Expand(Storage(), Value, expression, recursive: true, includeHidden: hidden, MaxDepth)
                            .Select(match => (PyObject)new PyPath(Join(Value, match), _fileSystem, MaxDepth))]);
                });

            case "match" or "full_match":
                return Builtin(name, arguments =>
                {
                    var pattern = Text(arguments[0]);

                    // `match` anchors at the right — `Path('a/b/c.py').match('*.py')` is
                    // true — while `full_match` has to account for the whole path.
                    if (name == "full_match")
                    {
                        return PyBool.Of(FullMatch(Value, pattern));
                    }

                    if (pattern.StartsWith('/'))
                    {
                        return PyBool.Of(FullMatch(Value, pattern));
                    }

                    var parts = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var mine = Value.Split('/', StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length > mine.Length)
                    {
                        return PyBool.False;
                    }

                    var offset = mine.Length - parts.Length;

                    return PyBool.Of(parts
                        .Select((part, index) => Globbing.Matches(mine[offset + index], part, segment: true))
                        .All(static matched => matched));
                });

            case "walk":
                return new PyBuiltinFunction(name, (arguments, keywords) =>
                {
                    var topDown = arguments.Length > 0 ? arguments[0].IsTruthy()
                        : keywords?.TryGetValue(new PyStr("top_down"), out var flag) != true || flag!.IsTruthy();

                    var depth = keywords?.TryGetValue(new PyStr("max_depth"), out var bound) == true
                        && bound is not PyNone
                            ? bound is PyInt { Value: var levels } && levels >= 0
                                ? (int)levels
                                : throw new PyRaise(PyErrors.ValueError("max_depth must not be negative"))
                            : (int?)null;

                    // The same traversal `os.walk` performs, differing only in that the
                    // directory comes back as a Path. Sharing it is what keeps the two
                    // orderings — and the pruning — from drifting apart.
                    return new PyIterator(
                        OsModule.Walk(Storage(), Value, topDown, onError: null, machine: null, depth, MaxDepth)
                            .Select(triple => (PyObject)new PyTuple([
                                new PyPath(((PyTuple)triple).Items[0].Display(), _fileSystem, MaxDepth),
                                ((PyTuple)triple).Items[1],
                                ((PyTuple)triple).Items[2]])),
                        "generator");
                });

            case "resolve" or "absolute":
                return Builtin(name, _ => new PyPath(
                    Value.StartsWith('/') ? Value : Join(Storage().WorkingDirectory, Value),
                    _fileSystem,
                    MaxDepth));

            default:
                return null;
        }
    }

    /// <summary>Whether a whole path matches a pattern, segment by segment.</summary>
    private static bool FullMatch(string value, string pattern)
    {
        var parts = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var mine = value.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // An absolute pattern only matches an absolute path.
        if (pattern.StartsWith('/') != value.StartsWith('/'))
        {
            return false;
        }

        return Segments(mine, 0, parts, 0);
    }

    /// <summary>Matches path segments against pattern segments, letting <c>**</c> span any number.</summary>
    private static bool Segments(string[] path, int pathIndex, string[] pattern, int patternIndex)
    {
        while (true)
        {
            if (patternIndex == pattern.Length)
            {
                return pathIndex == path.Length;
            }

            if (pattern[patternIndex] == "**")
            {
                // Try every split point: `**` may stand for no segments at all.
                for (var skip = pathIndex; skip <= path.Length; skip++)
                {
                    if (Segments(path, skip, pattern, patternIndex + 1))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (pathIndex == path.Length || !Globbing.Matches(path[pathIndex], pattern[patternIndex], segment: true))
            {
                return false;
            }

            pathIndex++;
            patternIndex++;
        }
    }

    private IPyFileSystem Storage() =>
        _fileSystem ?? throw new PyRaise(new PyException(
            PyExceptionType.OSError,
            "no filesystem is available to this interpreter"));

    /// <summary>Reads a flag given positionally or by name, for truth.</summary>
    private static bool Flag(PyObject[] arguments, PyDict? keywords, int position, string name) =>
        (arguments.Length > position ? arguments[position]
            : keywords?.TryGetValue(new PyStr(name), out var value) == true ? value
            : null)?.IsTruthy()
        ?? false;

    private static PyObject Builtin(string name, Func<PyObject[], PyObject> body) =>
        new PyBuiltinFunction(name, (arguments, _) => body(arguments));

    private static string Text(PyObject value) => value switch
    {
        PyStr text => text.Value,
        PyPath path => path.Value,
        _ => throw new PyRaise(PyErrors.TypeError(
            $"argument should be a str or an os.PathLike object, not {value.TypeName}")),
    };
}

/// <summary>The <c>pathlib</c> module.</summary>
public static class PathlibModule
{
    /// <summary>Builds the module over <paramref name="fileSystem"/>, which may be absent.</summary>
    /// <param name="fileSystem">The storage a path's I/O methods use.</param>
    /// <param name="maxDepth">How deep <c>glob</c> and <c>walk</c> may descend.</param>
    public static PyModuleObject Create(IPyFileSystem? fileSystem, int maxDepth = PyPath.DefaultMaxDepth)
    {
        var module = new PyModuleObject("pathlib");

        // A type rather than a factory function, so `isinstance(p, Path)` and
        // `type(p) is Path` are both true — scripts check.
        var path = new PyType(
            "pathlib.PosixPath",
            static value => value is PyPath,
            (arguments, _) => Construct(arguments, fileSystem, maxDepth));

        // Registering it makes `type(p)` resolve to the same object the module exposes.
        TypeRegistry.All["PosixPath"] = path;

        module.Add("Path", path);
        module.Add("PurePath", path);
        module.Add("PosixPath", path);
        module.Add("PurePosixPath", path);
        return module;
    }

    /// <summary>Builds a path from the constructor's segments.</summary>
    /// <param name="arguments">The segments, joined left to right.</param>
    /// <param name="fileSystem">The storage the result's I/O methods use.</param>
    /// <param name="maxDepth">How deep the result's traversals may descend.</param>
    public static PyObject Construct(
        PyObject[] arguments,
        IPyFileSystem? fileSystem,
        int maxDepth = PyPath.DefaultMaxDepth)
    {
        var value = ".";

        foreach (var argument in arguments)
        {
            var text = argument switch
            {
                PyStr str => str.Value,
                PyPath other => other.Value,
                _ => throw new PyRaise(PyErrors.TypeError(
                    $"argument should be a str or an os.PathLike object where __fspath__ "
                    + $"returns a str, not {argument.TypeName}")),
            };

            value = value == "." ? PyPath.Normalize(text) : PyPath.Join(value, text);
        }

        return new PyPath(value, fileSystem, maxDepth);
    }
}

/// <summary>
/// The result of <c>Path.stat()</c>.
/// </summary>
/// <remarks>
/// Indexable as well as named, because <c>st[6]</c> is how older code reads the size and
/// scripts still do it.
/// </remarks>
public sealed class PyStat : PyObject
{
    private readonly PyObject[] _fields;

    /// <summary>Creates a stat result.</summary>
    /// <param name="mode">The mode bits, including the file type.</param>
    /// <param name="size">The size in bytes.</param>
    /// <param name="modifiedAt">The modification time, in Unix seconds.</param>
    public PyStat(int mode, long size, double modifiedAt)
    {
        Mode = mode;
        Size = size;
        ModifiedAt = modifiedAt;

        // The tuple layout POSIX fixes: mode, ino, dev, nlink, uid, gid, size, atime,
        // mtime, ctime.
        _fields =
        [
            PyInt.From(mode), PyInt.From(0), PyInt.From(0), PyInt.From(1), PyInt.From(0), PyInt.From(0),
            PyInt.From(size), new PyFloat(modifiedAt), new PyFloat(modifiedAt), new PyFloat(modifiedAt),
        ];
    }

    /// <summary>The mode bits.</summary>
    public int Mode { get; }

    /// <summary>The size in bytes.</summary>
    public long Size { get; }

    /// <summary>The modification time.</summary>
    public double ModifiedAt { get; }

    /// <inheritdoc />
    public override string TypeName => "os.stat_result";

    /// <inheritdoc />
    public override string Repr() =>
        $"os.stat_result(st_mode={Mode}, st_size={Size}, st_mtime={ModifiedAt})";

    /// <inheritdoc />
    public override int? Length() => _fields.Length;

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate() => _fields;

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index) =>
        index is PyInt position && position.ToIndex() >= 0 && position.ToIndex() < _fields.Length
            ? _fields[position.ToIndex()]
            : throw new PyRaise(PyErrors.IndexError("tuple index out of range"));

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "st_mode" => PyInt.From(Mode),
        "st_size" => PyInt.From(Size),
        "st_mtime" => new PyFloat(ModifiedAt),
        "st_atime" => new PyFloat(ModifiedAt),
        "st_ctime" => new PyFloat(ModifiedAt),
        "st_ino" => PyInt.From(0),
        "st_dev" => PyInt.From(0),
        "st_nlink" => PyInt.From(1),
        "st_uid" => PyInt.From(0),
        "st_gid" => PyInt.From(0),
        _ => null,
    };
}
