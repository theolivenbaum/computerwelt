using System.Numerics;
using System.Text;
using Monty.Runtime;

namespace Monty.Modules;

/// <summary>
/// The <c>os</c> module and the <c>open</c> builtin.
/// </summary>
/// <remarks>
/// <para>
/// The module exists only when the host supplies an <see cref="IPyFileSystem"/>. That is
/// the sandbox's central promise: a program cannot reach storage the host did not hand it,
/// and <c>import os</c> failing outright is a clearer statement of that than an <c>os</c>
/// whose every call is denied.
/// </para>
/// <para>
/// The constants report POSIX values regardless of the machine underneath, matching the
/// virtual filesystem's own path semantics.
/// </para>
/// </remarks>
public static class OsModule
{
    /// <summary>Builds the <c>os</c> module over <paramref name="fileSystem"/>.</summary>
    public static PyModuleObject Create(IPyFileSystem fileSystem)
    {
        var module = new PyModuleObject("os");

        module.Add("sep", new PyStr("/"));
        module.Add("altsep", PyNone.Instance);
        module.Add("extsep", new PyStr("."));
        module.Add("pathsep", new PyStr(":"));
        module.Add("curdir", new PyStr("."));
        module.Add("pardir", new PyStr(".."));
        module.Add("linesep", new PyStr("\n"));
        module.Add("name", new PyStr("posix"));
        module.Add("devnull", new PyStr("/dev/null"));

        module.Add("environ", Environ(fileSystem));

        module.Add("getenv", new PyBuiltinFunction("getenv", (arguments, _) =>
        {
            // The name must be a str: passing anything else is a mistake worth reporting,
            // not a lookup that silently misses.
            if (arguments.Length == 0 || arguments[0] is not PyStr key)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"str expected, not {(arguments.Length == 0 ? "NoneType" : arguments[0].TypeName)}"));
            }

            return fileSystem.Environment.TryGetValue(key.Value, out var value)
                ? new PyStr(value)
                : arguments.Length > 1 ? arguments[1] : PyNone.Instance;
        }));

        module.Add("fspath", new PyBuiltinFunction("fspath", (arguments, keywords) =>
        {
            if (arguments.Length > 1)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"fspath() takes at most 1 argument ({arguments.Length} given)"));
            }

            if (arguments.Length == 0 && keywords is { Count: > 1 })
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"fspath() takes at most 1 keyword argument ({keywords.Count} given)"));
            }

            var value = arguments.Length > 0 ? arguments[0] : Keyword(keywords, "path");

            return value switch
            {
                PyStr or PyBytes => value,
                PyPath path => new PyStr(path.Value),
                null => throw new PyRaise(PyErrors.TypeError(
                    "fspath() missing required argument 'path' (pos 1)")),
                _ => throw new PyRaise(PyErrors.TypeError(
                    $"expected str, bytes or os.PathLike object, not {value.TypeName}")),
            };
        }));

        Add(module, "getcwd", 0, 0, (_, _) => new PyStr(fileSystem.WorkingDirectory));
        Add(module, "listdir", 0, 1, (arguments, _) => new PyList(
            [.. fileSystem.List(PathOf(arguments, 0, fileSystem.WorkingDirectory))
                .OrderBy(static name => name, StringComparer.Ordinal)
                .Select(static name => (PyObject)new PyStr(name))]));

        Add(module, "mkdir", 1, 2, (arguments, _) =>
        {
            fileSystem.CreateDirectory(PathOf(arguments, 0, "."), parents: false, existsOk: false);
            return PyNone.Instance;
        });

        Add(module, "makedirs", 1, 2, (arguments, keywords) =>
        {
            fileSystem.CreateDirectory(
                PathOf(arguments, 0, "."),
                parents: true,
                existsOk: Keyword(keywords, "exist_ok")?.IsTruthy() ?? false);

            return PyNone.Instance;
        });

        Add(module, "remove", 1, 1, (arguments, _) =>
        {
            fileSystem.Remove(PathOf(arguments, 0, "."));
            return PyNone.Instance;
        });

        Add(module, "unlink", 1, 1, (arguments, _) =>
        {
            fileSystem.Remove(PathOf(arguments, 0, "."));
            return PyNone.Instance;
        });

        Add(module, "rmdir", 1, 1, (arguments, _) =>
        {
            fileSystem.RemoveDirectory(PathOf(arguments, 0, "."));
            return PyNone.Instance;
        });

        Add(module, "rename", 2, 2, (arguments, _) =>
        {
            fileSystem.Rename(PathOf(arguments, 0, "."), PathOf(arguments, 1, "."));
            return PyNone.Instance;
        });

        module.Add("path", CreatePath(fileSystem));
        return module;
    }

    /// <summary>Builds <c>os.path</c>, whose operations are lexical except for the queries.</summary>
    private static PyModuleObject CreatePath(IPyFileSystem fileSystem)
    {
        var path = new PyModuleObject("posixpath");

        path.Add("sep", new PyStr("/"));

        Add(path, "join", 1, int.MaxValue, (arguments, _) =>
            new PyStr(arguments.Skip(1).Aggregate(
                Text(arguments[0]),
                (left, part) => PyPath.Join(left, Text(part)))));

        Add(path, "basename", 1, 1, (arguments, _) => new PyStr(new PyPath(Text(arguments[0])).Name));
        Add(path, "dirname", 1, 1, (arguments, _) =>
        {
            var value = Text(arguments[0]);
            var slash = value.TrimEnd('/').LastIndexOf('/');

            // Unlike Path.parent, dirname of a bare name is the empty string.
            return new PyStr(slash switch
            {
                < 0 => string.Empty,
                0 => "/",
                _ => value.TrimEnd('/')[..slash],
            });
        });

        Add(path, "splitext", 1, 1, (arguments, _) =>
        {
            var value = Text(arguments[0]);
            var name = new PyPath(value);
            var suffix = name.Suffix;

            return new PyTuple([
                new PyStr(suffix.Length == 0 ? value : value[..^suffix.Length]),
                new PyStr(suffix),
            ]);
        });

        Add(path, "split", 1, 1, (arguments, _) =>
        {
            var value = Text(arguments[0]);
            var name = new PyPath(value);
            var parent = name.Parent;

            return new PyTuple([new PyStr(parent == "." ? string.Empty : parent), new PyStr(name.Name)]);
        });

        Add(path, "abspath", 1, 1, (arguments, _) =>
        {
            var value = Text(arguments[0]);
            return new PyStr(value.StartsWith('/') ? PyPath.Normalize(value) : PyPath.Join(fileSystem.WorkingDirectory, value));
        });

        Add(path, "normpath", 1, 1, (arguments, _) => new PyStr(PyPath.Normalize(Text(arguments[0]))));
        Add(path, "isabs", 1, 1, (arguments, _) => PyBool.Of(Text(arguments[0]).StartsWith('/')));
        Add(path, "exists", 1, 1, (arguments, _) => PyBool.Of(fileSystem.Exists(Text(arguments[0]))));
        Add(path, "isfile", 1, 1, (arguments, _) => PyBool.Of(fileSystem.IsFile(Text(arguments[0]))));
        Add(path, "isdir", 1, 1, (arguments, _) => PyBool.Of(fileSystem.IsDirectory(Text(arguments[0]))));
        Add(path, "islink", 1, 1, (_, _) => PyBool.False);
        Add(path, "getsize", 1, 1, (arguments, _) => new PyInt(fileSystem.Size(Text(arguments[0]))));

        return path;
    }

    /// <summary>
    /// Builds <c>os.environ</c>.
    /// </summary>
    /// <remarks>
    /// A plain dict rather than a live mapping: assigning to it changes nothing outside the
    /// program, and pretending otherwise by accepting writes that vanish would be worse
    /// than a snapshot.
    /// </remarks>
    private static PyDict Environ(IPyFileSystem fileSystem)
    {
        var environ = new PyDict();

        foreach (var (name, value) in fileSystem.Environment)
        {
            environ.Set(new PyStr(name), new PyStr(value));
        }

        return environ;
    }

    /// <summary>Builds the <c>open</c> builtin over <paramref name="fileSystem"/>.</summary>
    public static PyObject CreateOpen(IPyFileSystem fileSystem) =>
        new PyBuiltinFunction("open", (arguments, keywords) => Open(fileSystem, arguments, keywords));

    /// <summary>Runs <c>open</c>'s argument binding and opens the file.</summary>
    /// <remarks>
    /// <c>Path.open</c> is the same call with the path already bound as <c>file</c>, so it
    /// shares this body rather than repeating the binder and its rejections.
    /// </remarks>
    /// <param name="fileSystem">Where the file lives.</param>
    /// <param name="arguments">The positional arguments, <c>file</c> first.</param>
    /// <param name="keywords">The keyword arguments, if any.</param>
    /// <returns>The open file.</returns>
    public static PyObject Open(IPyFileSystem fileSystem, PyObject[] arguments, PyDict? keywords)
    {
        string[] names = ["file", "mode", "buffering", "encoding", "errors", "newline", "closefd", "opener"];

        if (arguments.Length > names.Length)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"open() takes at most {names.Length} arguments ({arguments.Length} given)"));
        }

        var given = new PyObject?[names.Length];

        for (var i = 0; i < arguments.Length; i++)
        {
            given[i] = arguments[i];
        }

        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            var position = Array.IndexOf(names, key.Display());

            if (position < 0)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"open() got an unexpected keyword argument '{key.Display()}'"));
            }

            if (given[position] is not null)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"open() got multiple values for argument '{names[position]}'"));
            }

            given[position] = value;
        }

        // Everything past the mode is accepted only at its CPython default: honouring
        // one silently would promise behaviour this wrapper does not have. UTF-8 is
        // the exception, because that is already what it does.
        Default(given[2], "buffering", static value => value is PyInt { Value: var n } && n == -1);
        if (given[3] is not (null or PyNone or PyStr))
        {
            throw new PyRaise(PyErrors.TypeError(
                $"open() argument 'encoding' must be str or None, not {given[3]!.TypeName}"));
        }

        Default(given[3], "encoding", static value =>
            value is PyNone or PyStr { Value: "utf-8" or "utf8" or "UTF-8" });
        Default(given[4], "errors", static value => value is PyNone);
        Default(given[5], "newline", static value => value is PyNone);
        Default(given[6], "closefd", static value => value.IsTruthy());
        Default(given[7], "opener", static value => value is PyNone);

        var mode = given[1] switch
        {
            null or PyStr => (given[1] as PyStr)?.Value ?? "r",

            // The argument clinic spells a lone None as "None", not "NoneType".
            PyNone => throw new PyRaise(PyErrors.TypeError(
                "open() argument 'mode' must be str, not None")),
            _ => throw new PyRaise(PyErrors.TypeError(
                $"open() argument 'mode' must be str, not {given[1]!.TypeName}")),
        };

        return new PyFile(fileSystem, Text(given[0] ?? PyNone.Instance), mode);
    }

    /// <summary>Rejects an argument this wrapper accepts only at its default.</summary>
    private static void Default(PyObject? value, string name, Func<PyObject, bool> isDefault)
    {
        if (value is not null && !isDefault(value))
        {
            throw new PyRaise(PyErrors.TypeError($"'{name}' argument is not yet supported"));
        }
    }

    private static PyObject? Keyword(PyDict? keywords, string name) =>
        keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) ? value : null;

    private static string PathOf(PyObject[] arguments, int index, string fallback) =>
        index < arguments.Length ? Text(arguments[index]) : fallback;

    private static string Text(PyObject value) => value switch
    {
        PyStr text => text.Value,
        PyPath path => path.Value,
        PyBytes bytes => Encoding.UTF8.GetString(bytes.Value),
        _ => throw new PyRaise(PyErrors.TypeError(
            $"expected str, bytes or os.PathLike object, not {value.TypeName}")),
    };

    private static void Add(
        PyModuleObject module,
        string name,
        int minimum,
        int maximum,
        Func<PyObject[], PyDict?, PyObject> body)
    {
        module.Add(name, new PyBuiltinFunction(name, (arguments, keywords) =>
        {
            if (arguments.Length < minimum || arguments.Length > maximum)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{name} expected at least {minimum} arguments, got {arguments.Length}"));
            }

            return body(arguments, keywords);
        }));
    }
}

/// <summary>
/// An open file.
/// </summary>
/// <remarks>
/// Writes are flushed as they are made rather than at close, because a sandboxed program
/// may be abandoned mid-run and there is no finalizer to fall back on — buffering would
/// mean silently losing what a script believed it had written.
/// </remarks>
public sealed class PyFile : PyObject
{
    private readonly IPyFileSystem _fileSystem;
    private readonly string _path;
    private readonly bool _binary;
    private bool _closed;
    private int _position;

    /// <summary>Opens a file.</summary>
    /// <param name="fileSystem">Where the file lives.</param>
    /// <param name="path">Its path.</param>
    /// <param name="mode">The mode string, as <c>open</c> takes it.</param>
    public PyFile(IPyFileSystem fileSystem, string path, string mode)
    {
        _fileSystem = fileSystem;
        _path = path;
        _binary = mode.Contains('b', StringComparison.Ordinal);
        Mode = mode;

        // The mode is parsed before anything is opened: an unknown letter, or a mode with
        // no action in it at all, is a mistake in the call.
        foreach (var letter in mode)
        {
            if (!"rwaxbt+U".Contains(letter, StringComparison.Ordinal))
            {
                throw new PyRaise(PyErrors.ValueError($"invalid mode: '{mode}'"));
            }
        }

        var actions = mode.Count(static letter => letter is 'r' or 'w' or 'a' or 'x');

        // Two actions and no action are different mistakes, and CPython words them
        // differently — down to the case of the first letter.
        if (actions > 1)
        {
            throw new PyRaise(PyErrors.ValueError(
                "must have exactly one of create/read/write/append mode"));
        }

        if (actions == 0 || mode.Count(static letter => letter == '+') > 1)
        {
            throw new PyRaise(PyErrors.ValueError(
                "Must have exactly one of create/read/write/append mode and at most one plus"));
        }

        // An update mode would need a read position that survives a write, which this
        // wrapper does not keep — so it is refused rather than silently truncating.
        if (mode.Contains('+', StringComparison.Ordinal))
        {
            throw new PyRaise(PyErrors.ValueError("update modes ('+') are not yet supported"));
        }

        var write = mode.Contains('w', StringComparison.Ordinal);
        var append = mode.Contains('a', StringComparison.Ordinal);
        var exclusive = mode.Contains('x', StringComparison.Ordinal);

        if (exclusive && fileSystem.Exists(path))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.FileExistsError,
                $"[Errno 17] File exists: '{path}'"));
        }

        if (write || exclusive)
        {
            fileSystem.Write(path, []);
            return;
        }

        if (append)
        {
            if (!fileSystem.Exists(path))
            {
                fileSystem.Write(path, []);
            }

            return;
        }

        // Reading a file that does not exist has to fail now, not at the first read, and
        // a directory is not a file at all.
        if (fileSystem.IsDirectory(path))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.IsADirectoryError,
                $"[Errno 21] Is a directory: '{path}'"));
        }

        if (!fileSystem.Exists(path))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.FileNotFoundError,
                $"[Errno 2] No such file or directory: '{path}'"));
        }

        Appending = false;
    }

    /// <summary>Whether writes append rather than overwrite.</summary>
    private bool Appending { get; }

    /// <summary>The mode the file was opened with.</summary>
    public string Mode { get; }

    /// <summary>Whether the mode permits reading.</summary>
    private bool Readable => Mode.Contains('r', StringComparison.Ordinal);

    /// <summary>Whether the mode permits writing.</summary>
    private bool Writable =>
        Mode.Contains('w', StringComparison.Ordinal)
        || Mode.Contains('a', StringComparison.Ordinal)
        || Mode.Contains('x', StringComparison.Ordinal);

    /// <inheritdoc />
    /// <remarks>
    /// A binary file is a reader or a writer depending on its mode; a text one is a
    /// TextIOWrapper either way.
    /// </remarks>
    public override string TypeName => !_binary ? "_io.TextIOWrapper"
        : Mode.Contains('r', StringComparison.Ordinal) ? "_io.BufferedReader"
        : "_io.BufferedWriter";

    /// <inheritdoc />
    public override string Repr() => $"<{TypeName} name='{_path}' mode='{Mode}'>";

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate() => Lines().Select(static line => (PyObject)new PyStr(line));

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name)
    {
        // Every operation but `close`, `closed` and the context-manager exit refuses once
        // the file is closed.
        if (_closed && name is not ("closed" or "close" or "name" or "mode" or "__exit__"))
        {
            return new PyBuiltinFunction(name, _ =>
                throw new PyRaise(PyErrors.ValueError("I/O operation on closed file.")));
        }

        return name switch
        {
        "name" => new PyStr(_path),
        "closed" => PyBool.Of(_closed),
        "mode" => new PyStr(Mode),
        "readable" => new PyBuiltinFunction("readable", _ => PyBool.Of(Readable)),
        "writable" => new PyBuiltinFunction("writable", _ => PyBool.Of(Writable)),

        "seekable" => new PyBuiltinFunction("seekable", _ => PyBool.True),

        "read" => new PyBuiltinFunction("read", arguments =>
        {
            if (!Readable)
            {
                throw new PyRaise(new PyException(
                    PyExceptionType.UnsupportedOperation, _binary ? "read" : "not readable"));
            }

            var content = Content();
            var available = _position >= content.Length ? string.Empty : content[_position..];

            // A size of None or a negative one reads the rest; anything that is not an
            // integer at all is a TypeError rather than a silent read-everything.
            if (arguments.Length > 0 && arguments[0] is not (PyInt or PyNone))
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"'{arguments[0].TypeName}' object cannot be interpreted as an integer"));
            }

            var text = arguments.Length > 0 && arguments[0] is PyInt { Value: var size } && size >= 0
                ? available[..(int)BigInteger.Min(size, available.Length)]
                : available;

            _position += text.Length;
            return _binary ? new PyBytes(Encoding.UTF8.GetBytes(text)) : new PyStr(text);
        }),

        "readline" => new PyBuiltinFunction("readline", _ =>
        {
            var content = Content();

            if (_position >= content.Length)
            {
                return Piece(string.Empty);
            }

            var newline = content.IndexOf('\n', _position);
            var end = newline < 0 ? content.Length : newline + 1;
            var line = content[_position..end];
            _position = end;
            return Piece(line);
        }),

        "readlines" => new PyBuiltinFunction("readlines", _ =>
            new PyList([.. Lines().Select(Piece)])),

        "tell" => new PyBuiltinFunction("tell", _ => new PyInt(_position)),

        "seek" => new PyBuiltinFunction("seek", arguments =>
        {
            var offset = arguments.Length > 0 && arguments[0] is PyInt position ? (int)position.Value : 0;
            var whence = arguments.Length > 1 && arguments[1] is PyInt from ? (int)from.Value : 0;

            if (whence is not (0 or 1 or 2))
            {
                throw new PyRaise(PyErrors.ValueError($"whence value {whence} unsupported"));
            }

            var target = whence switch
            {
                1 => _position + offset,
                2 => Content().Length + offset,
                _ => offset,
            };

            // Seeking before the start is an error, not a clamp.
            if (target < 0)
            {
                throw new PyRaise(new PyException(
                    PyExceptionType.OSError, "[Errno 22] Invalid argument"));
            }

            _position = target;
            return new PyInt(_position);
        }),

        "write" => new PyBuiltinFunction("write", arguments =>
        {
            if (!Writable)
            {
                throw new PyRaise(new PyException(
                    PyExceptionType.UnsupportedOperation, _binary ? "write" : "not writable"));
            }

            if (_binary && arguments[0] is not PyBytes)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"a bytes-like object is required, not '{arguments[0].TypeName}'"));
            }

            if (!_binary && arguments[0] is PyBytes)
            {
                throw new PyRaise(PyErrors.TypeError("write() argument must be str, not bytes"));
            }

            var payload = arguments[0] switch
            {
                PyBytes bytes => bytes.Value,
                var value => Encoding.UTF8.GetBytes(value.Display()),
            };

            _fileSystem.Append(_path, payload);

            // The position advances by what was written, so tell() tracks a write-only
            // file too.
            var written = arguments[0] is PyBytes ? payload.Length : arguments[0].Display().Length;
            _position += written;

            return new PyInt(written);
        }),

        "writelines" => new PyBuiltinFunction("writelines", arguments =>
        {
            foreach (var line in VirtualMachine.RequireIterable(arguments[0]))
            {
                _fileSystem.Append(_path, Encoding.UTF8.GetBytes(line.Display()));
            }

            return PyNone.Instance;
        }),

        "close" => new PyBuiltinFunction("close", _ =>
        {
            _closed = true;
            return PyNone.Instance;
        }),

        "flush" => new PyBuiltinFunction("flush", _ => PyNone.Instance),

        // `with open(...) as f` needs the context-manager protocol on the file itself.
        "__enter__" => new PyBuiltinFunction("__enter__", _ => this),
        // Closing on the way out, and returning None rather than False: a file does not
        // suppress an exception, and `f.__exit__(None, None, None)` is None.
        "__exit__" => new PyBuiltinFunction("__exit__", _ =>
        {
            _closed = true;
            return PyNone.Instance;
        }),

        _ => null,
        };
    }

    /// <summary>Wraps a piece of the content as the mode's type.</summary>
    private PyObject Piece(string text) =>
        _binary ? new PyBytes(Encoding.UTF8.GetBytes(text)) : new PyStr(text);

    private string Content() => Encoding.UTF8.GetString(_fileSystem.Read(_path));

    private IEnumerable<string> Lines()
    {
        var content = Content();
        var start = _position;

        // A position past the end leaves it there rather than snapping back to the length.
        _position = Math.Max(_position, content.Length);

        while (start < content.Length)
        {
            var newline = content.IndexOf('\n', start);
            var end = newline < 0 ? content.Length : newline + 1;
            yield return content[start..end];
            start = end;
        }
    }
}
