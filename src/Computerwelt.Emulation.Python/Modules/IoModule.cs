using System.Numerics;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>The <c>io</c> module.</summary>
/// <remarks>
/// <para>
/// Only the three names a sandboxed script actually reaches for: <c>open</c>, which is the
/// builtin under its other spelling, and the two in-memory streams. The class hierarchy
/// (<c>IOBase</c>, <c>RawIOBase</c>, …) is deliberately absent — nothing here can be
/// subclassed usefully, so exposing the names would promise an interface that does not
/// work.
/// </para>
/// <para>
/// <c>io.UnsupportedOperation</c> is re-exported here because that is where CPython
/// defines it, and a script that catches it by name expects to find it on this module.
/// </para>
/// </remarks>
public static class IoModule
{
    /// <summary>Builds the module.</summary>
    /// <param name="fileSystem">
    /// The filesystem <c>io.open</c> works over, or <see langword="null"/> when the sandbox
    /// has none — in which case the module offers the memory streams only, exactly as the
    /// builtin <c>open</c> is absent.
    /// </param>
    public static PyModuleObject Create(IPyFileSystem? fileSystem)
    {
        var module = new PyModuleObject("io");

        if (fileSystem is not null)
        {
            // The same function object the builtin `open` is, so `io.open is open` holds
            // the way CPython's does in spirit: one implementation, two names.
            module.Add("open", OsModule.CreateOpen(fileSystem));
        }

        module.Add("StringIO", new PyBuiltinFunction("StringIO", arguments =>
            new PyMemoryStream(binary: false, Initial(arguments, binary: false))));

        module.Add("BytesIO", new PyBuiltinFunction("BytesIO", arguments =>
            new PyMemoryStream(binary: true, Initial(arguments, binary: true))));

        module.Add("UnsupportedOperation", PyExceptionType.UnsupportedOperation);

        return module;
    }

    /// <summary>Reads the optional initial-contents argument.</summary>
    private static string Initial(PyObject[] arguments, bool binary)
    {
        if (arguments.Length == 0 || arguments[0] is PyNone)
        {
            return string.Empty;
        }

        return (binary, arguments[0]) switch
        {
            (true, PyBytes bytes) => Encoding.UTF8.GetString(bytes.Value),
            (false, PyStr text) => text.Value,
            (true, var other) => throw new PyRaise(PyErrors.TypeError(
                $"a bytes-like object is required, not '{other.TypeName}'")),
            (false, var other) => throw new PyRaise(PyErrors.TypeError(
                $"initial_value must be str or None, not {other.TypeName}")),
        };
    }
}

/// <summary>An <c>io.StringIO</c> or <c>io.BytesIO</c>: a file that lives in memory.</summary>
/// <remarks>
/// Both are one class because the only difference is whether a read hands back
/// <c>str</c> or <c>bytes</c>. The buffer is held as a string and encoded at the edge, the
/// same choice <see cref="PyFile"/> makes, so the two behave identically on the same data.
/// </remarks>
public sealed class PyMemoryStream : PyObject
{
    private readonly bool _binary;
    private StringBuilder _buffer;
    private bool _closed;
    private int _position;

    /// <summary>Creates a stream over <paramref name="initial"/>.</summary>
    /// <param name="binary">True for <c>BytesIO</c>, false for <c>StringIO</c>.</param>
    /// <param name="initial">The contents it starts with.</param>
    public PyMemoryStream(bool binary, string initial = "")
    {
        _binary = binary;
        _buffer = new StringBuilder(initial);
    }

    /// <inheritdoc />
    public override string TypeName => _binary ? "_io.BytesIO" : "_io.StringIO";

    /// <inheritdoc />
    public override string Repr() => $"<{TypeName} object>";

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate()
    {
        // Iteration consumes the stream from the current position, as it does on a file.
        while (ReadLine() is { Length: > 0 } line)
        {
            yield return Piece(line);
        }
    }

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name)
    {
        if (_closed && name is not ("closed" or "close" or "__exit__"))
        {
            return new PyBuiltinFunction(name, _ =>
                throw new PyRaise(PyErrors.ValueError("I/O operation on closed file.")));
        }

        return name switch
        {
            "closed" => PyBool.Of(_closed),
            "readable" => new PyBuiltinFunction("readable", _ => PyBool.True),
            "writable" => new PyBuiltinFunction("writable", _ => PyBool.True),
            "seekable" => new PyBuiltinFunction("seekable", _ => PyBool.True),

            "getvalue" => new PyBuiltinFunction("getvalue", _ => Piece(_buffer.ToString())),

            "read" => new PyBuiltinFunction("read", arguments =>
            {
                var content = _buffer.ToString();
                var available = _position >= content.Length ? string.Empty : content[_position..];

                if (arguments.Length > 0 && arguments[0] is not (PyInt or PyNone))
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"'{arguments[0].TypeName}' object cannot be interpreted as an integer"));
                }

                var text = arguments.Length > 0 && arguments[0] is PyInt { Value: var size } && size >= 0
                    ? available[..(int)BigInteger.Min(size, available.Length)]
                    : available;

                _position += text.Length;
                return Piece(text);
            }),

            "readline" => new PyBuiltinFunction("readline", _ => Piece(ReadLine())),

            "readlines" => new PyBuiltinFunction("readlines", _ =>
            {
                var lines = new List<PyObject>();

                while (ReadLine() is { Length: > 0 } line)
                {
                    lines.Add(Piece(line));
                }

                return new PyList(lines);
            }),

            "write" => new PyBuiltinFunction("write", arguments =>
            {
                var text = Text(arguments.Length > 0 ? arguments[0] : PyNone.Instance);

                // Writing past the end zero-fills, as CPython does, so a seek-then-write
                // does not silently move the data somewhere else.
                if (_position > _buffer.Length)
                {
                    _buffer.Append('\0', _position - _buffer.Length);
                }

                var overwritten = Math.Min(text.Length, _buffer.Length - _position);
                _buffer.Remove(_position, overwritten).Insert(_position, text);
                _position += text.Length;
                return PyInt.From(text.Length);
            }),

            "writelines" => new PyBuiltinFunction("writelines", arguments =>
            {
                foreach (var line in arguments.Length > 0 ? arguments[0].Iterate() ?? [] : [])
                {
                    var text = Text(line);
                    _buffer.Remove(_position, Math.Min(text.Length, _buffer.Length - _position))
                        .Insert(_position, text);
                    _position += text.Length;
                }

                return PyNone.Instance;
            }),

            "tell" => new PyBuiltinFunction("tell", _ => PyInt.From(_position)),

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
                    2 => _buffer.Length + offset,
                    _ => offset,
                };

                if (target < 0)
                {
                    throw new PyRaise(new PyException(
                        PyExceptionType.OSError, "[Errno 22] Invalid argument"));
                }

                _position = target;
                return PyInt.From(_position);
            }),

            "truncate" => new PyBuiltinFunction("truncate", arguments =>
            {
                var size = arguments.Length > 0 && arguments[0] is PyInt limit
                    ? (int)limit.Value
                    : _position;

                if (size < _buffer.Length)
                {
                    _buffer.Length = Math.Max(size, 0);
                }

                return PyInt.From(_buffer.Length);
            }),

            // Flushing is a no-op that succeeds: there is nothing between the write and
            // the buffer to flush.
            "flush" => new PyBuiltinFunction("flush", static _ => PyNone.Instance),

            "close" => new PyBuiltinFunction("close", _ =>
            {
                // CPython releases the buffer on close, so `getvalue()` afterwards fails
                // rather than returning stale contents.
                _closed = true;
                _buffer = new StringBuilder();
                return PyNone.Instance;
            }),

            "__enter__" => new PyBuiltinFunction("__enter__", _ => this),

            "__exit__" => new PyBuiltinFunction("__exit__", _ =>
            {
                _closed = true;
                _buffer = new StringBuilder();
                return PyNone.Instance;
            }),

            _ => null,
        };
    }

    /// <summary>Reads one line, newline included, or the empty string at the end.</summary>
    public string ReadLine()
    {
        var content = _buffer.ToString();

        if (_position >= content.Length)
        {
            return string.Empty;
        }

        var newline = content.IndexOf('\n', _position);
        var end = newline < 0 ? content.Length : newline + 1;
        var line = content[_position..end];
        _position = end;
        return line;
    }

    /// <summary>Hands back a chunk as the type this stream's mode reads.</summary>
    private PyObject Piece(string text) =>
        _binary ? new PyBytes(Encoding.UTF8.GetBytes(text)) : new PyStr(text);

    /// <summary>Takes a chunk in the type this stream's mode writes.</summary>
    private string Text(PyObject value) => (_binary, value) switch
    {
        (true, PyBytes bytes) => Encoding.UTF8.GetString(bytes.Value),
        (false, PyStr text) => text.Value,
        (true, var other) => throw new PyRaise(PyErrors.TypeError(
            $"a bytes-like object is required, not '{other.TypeName}'")),
        (false, var other) => throw new PyRaise(PyErrors.TypeError(
            $"string argument expected, got '{other.TypeName}'")),
    };
}
