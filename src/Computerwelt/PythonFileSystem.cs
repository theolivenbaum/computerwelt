using System.Text;
using Bashkit;
using Monty.Runtime;

namespace Computerwelt;

/// <summary>
/// Exposes the shell's virtual filesystem to Python as <c>os</c>, <c>pathlib</c> and
/// <c>open</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole point of porting both halves: <c>python script.py</c> inside a shell
/// script sees the same tree the shell does, so a pipeline can write a file with
/// <c>echo</c> and read it with <c>open()</c>. Neither side reaches the host.
/// </para>
/// <para>
/// The filesystem calls are async and Python's are not, so each one is awaited
/// synchronously here. That is safe because <see cref="InMemoryFileSystem"/> completes
/// synchronously; a genuinely asynchronous backend would need Monty's external-function
/// suspension instead, which is why this bridge is explicit rather than automatic.
/// </para>
/// </remarks>
public static class PythonFileSystem
{
    /// <summary>Builds the <c>os</c> module over <paramref name="fileSystem"/>.</summary>
    public static PyObject CreateOsModule(IFileSystem fileSystem, Func<VPath> workingDirectory)
    {
        var module = new Monty.Modules.PyModuleObject("os");

        module.Add("sep", new PyStr("/"));
        module.Add("linesep", new PyStr("\n"));
        module.Add("name", new PyStr("posix"));
        module.Add("curdir", new PyStr("."));
        module.Add("pardir", new PyStr(".."));

        // The environment is empty by default: a sandboxed script must not inherit the
        // host's variables, and the shell passes in only what it chooses to.
        module.Add("environ", new PyDict());

        module.Add("getcwd", _ => new PyStr(workingDirectory().Value));

        module.Add("listdir", arguments =>
        {
            var path = Resolve(workingDirectory(), arguments, 0, ".");
            var entries = Await(fileSystem.ReadDirectoryAsync(path));
            return new PyList([.. entries.Select(static e => (PyObject)new PyStr(e.Name))]);
        });

        module.Add("mkdir", arguments =>
        {
            Await(fileSystem.CreateDirectoryAsync(Resolve(workingDirectory(), arguments, 0, "."), recursive: false));
            return PyNone.Instance;
        });

        module.Add("makedirs", arguments =>
        {
            Await(fileSystem.CreateDirectoryAsync(Resolve(workingDirectory(), arguments, 0, "."), recursive: true));
            return PyNone.Instance;
        });

        module.Add("remove", arguments =>
        {
            Await(fileSystem.RemoveAsync(Resolve(workingDirectory(), arguments, 0, "."), recursive: false));
            return PyNone.Instance;
        });

        module.Add("rmdir", arguments =>
        {
            Await(fileSystem.RemoveAsync(Resolve(workingDirectory(), arguments, 0, "."), recursive: false));
            return PyNone.Instance;
        });

        module.Add("rename", arguments =>
        {
            Await(fileSystem.RenameAsync(
                Resolve(workingDirectory(), arguments, 0, "."),
                Resolve(workingDirectory(), arguments, 1, ".")));

            return PyNone.Instance;
        });

        var path = new Monty.Modules.PyModuleObject("os.path");

        path.Add("join", static arguments =>
        {
            var result = VPath.Parse(arguments.Length > 0 ? arguments[0].Display() : string.Empty);

            foreach (var part in arguments.Skip(1))
            {
                result = result.Join(part.Display());
            }

            return new PyStr(result.Value);
        });

        path.Add("basename", static arguments => new PyStr(VPath.Parse(arguments[0].Display()).FileName));

        path.Add("dirname", static arguments =>
            new PyStr(VPath.Parse(arguments[0].Display()).Parent?.Value ?? string.Empty));

        path.Add("exists", arguments =>
            PyBool.Of(Await(fileSystem.ExistsAsync(Resolve(workingDirectory(), arguments, 0, ".")))));

        path.Add("isfile", arguments => PyBool.Of(
            TryStat(fileSystem, Resolve(workingDirectory(), arguments, 0, ".")) is { IsFile: true }));

        path.Add("isdir", arguments => PyBool.Of(
            TryStat(fileSystem, Resolve(workingDirectory(), arguments, 0, ".")) is { IsDirectory: true }));

        path.Add("getsize", arguments => new PyInt(
            TryStat(fileSystem, Resolve(workingDirectory(), arguments, 0, "."))?.Size
            ?? throw new PyRaise(new PyException(
                PyExceptionType.FileNotFoundError, $"No such file or directory: '{arguments[0].Display()}'"))));

        path.Add("splitext", static arguments =>
        {
            var target = VPath.Parse(arguments[0].Display());
            var extension = target.Extension;
            var text = arguments[0].Display();

            return new PyTuple([
                new PyStr(extension.Length == 0 ? text : text[..^extension.Length]),
                new PyStr(extension),
            ]);
        });

        module.Add("path", path);
        return module;
    }

    /// <summary>Builds the <c>open</c> builtin over <paramref name="fileSystem"/>.</summary>
    public static PyObject CreateOpen(IFileSystem fileSystem, Func<VPath> workingDirectory) =>
        new PyBuiltinFunction("open", arguments =>
        {
            var path = Resolve(workingDirectory(), arguments, 0, ".");
            var mode = arguments.Length > 1 ? arguments[1].Display() : "r";

            return new PyFile(fileSystem, path, mode);
        });

    private static VPath Resolve(VPath cwd, PyObject[] arguments, int index, string fallback) =>
        VPath.Resolve(cwd, index < arguments.Length ? arguments[index].Display() : fallback);

    private static FileMetadata? TryStat(IFileSystem fileSystem, VPath path)
    {
        try
        {
            return Await(fileSystem.StatAsync(path));
        }
        catch (FileSystemException)
        {
            return null;
        }
    }

    /// <summary>
    /// Completes a filesystem call synchronously, translating its failure into the Python
    /// exception a script expects.
    /// </summary>
    internal static T Await<T>(ValueTask<T> task)
    {
        try
        {
            return task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
        }
        catch (FileSystemException e)
        {
            throw Translate(e);
        }
    }

    internal static void Await(ValueTask task)
    {
        try
        {
            if (task.IsCompletedSuccessfully)
            {
                task.GetAwaiter().GetResult();
                return;
            }

            task.AsTask().GetAwaiter().GetResult();
        }
        catch (FileSystemException e)
        {
            throw Translate(e);
        }
    }

    private static PyRaise Translate(FileSystemException error) => new(new PyException(
        error.FsKind switch
        {
            FileSystemErrorKind.NotFound => PyExceptionType.FileNotFoundError,
            FileSystemErrorKind.PermissionDenied or FileSystemErrorKind.ReadOnly => PyExceptionType.OSError,
            FileSystemErrorKind.AlreadyExists => PyExceptionType.OSError,
            _ => PyExceptionType.OSError,
        },
        error.Message));
}

/// <summary>A file handle over the virtual filesystem.</summary>
internal sealed class PyFile : PyObject
{
    private readonly IFileSystem _fileSystem;
    private readonly VPath _path;
    private readonly StringBuilder _pending = new();
    private readonly bool _writable;
    private string? _content;
    private int _position;
    private bool _closed;

    public PyFile(IFileSystem fileSystem, VPath path, string mode)
    {
        _fileSystem = fileSystem;
        _path = path;
        _writable = mode.Contains('w', StringComparison.Ordinal)
            || mode.Contains('a', StringComparison.Ordinal)
            || mode.Contains('+', StringComparison.Ordinal);
        if (mode.Contains('w', StringComparison.Ordinal))
        {
            // Opening for writing truncates immediately, so the file exists and is empty
            // even if nothing is ever written.
            PythonFileSystem.Await(fileSystem.WriteFileAsync(path, ReadOnlyMemory<byte>.Empty));
            _content = string.Empty;
        }
    }

    public override string TypeName => "TextIOWrapper";

    public override string Repr() => $"<open file '{_path}'>";

    public override IEnumerable<PyObject>? Iterate() =>
        ReadAll().Split('\n') is var lines
            ? lines.Where((_, i) => i < lines.Length - 1 || lines[^1].Length > 0)
                .Select((line, i) => (PyObject)new PyStr(i < lines.Length - 1 ? line + "\n" : line))
            : null;

    public override PyObject? GetAttribute(string name) => name switch
    {
        "read" => new PyBuiltinFunction("read", _ =>
        {
            var text = ReadAll()[_position..];
            _position = ReadAll().Length;
            return new PyStr(text);
        }),

        "readline" => new PyBuiltinFunction("readline", _ =>
        {
            var text = ReadAll();

            if (_position >= text.Length)
            {
                return PyStr.Empty;
            }

            var end = text.IndexOf('\n', _position);
            end = end < 0 ? text.Length : end + 1;
            var line = text[_position..end];
            _position = end;
            return new PyStr(line);
        }),

        "readlines" => new PyBuiltinFunction("readlines", _ =>
            new PyList([.. Iterate() ?? []])),

        // Writes go through immediately rather than being buffered until close. CPython
        // gets away with buffering because a dropped file object is finalized; there is no
        // finalization here, so `open(p, 'w').write(x)` would otherwise silently lose the
        // write.
        "write" => new PyBuiltinFunction("write", arguments =>
        {
            Require();
            var text = arguments[0].Display();
            _pending.Append(text);
            Flush();
            return new PyInt(text.Length);
        }),

        "writelines" => new PyBuiltinFunction("writelines", arguments =>
        {
            Require();

            foreach (var line in VirtualMachine.RequireIterable(arguments[0]))
            {
                _pending.Append(line.Display());
            }

            Flush();
            return PyNone.Instance;
        }),

        "close" => new PyBuiltinFunction("close", _ =>
        {
            Flush();
            _closed = true;
            return PyNone.Instance;
        }),

        "flush" => new PyBuiltinFunction("flush", _ =>
        {
            Flush();
            return PyNone.Instance;
        }),

        // `with open(...) as f:` needs the context-manager protocol.
        "__enter__" => new PyBuiltinFunction("__enter__", _ => this),
        "__exit__" => new PyBuiltinFunction("__exit__", _ =>
        {
            Flush();
            _closed = true;
            return PyBool.False;
        }),

        "name" => new PyStr(_path.Value),
        "closed" => PyBool.Of(_closed),
        _ => null,
    };

    private void Require()
    {
        if (!_writable)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.OSError, "not writable"));
        }
    }

    private string ReadAll() =>
        _content ??= Encoding.UTF8.GetString(PythonFileSystem.Await(_fileSystem.ReadFileAsync(_path)));

    private void Flush()
    {
        if (_pending.Length == 0)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(_pending.ToString());
        _pending.Clear();

        // Both modes append here: `w` already truncated the file when it was opened, so
        // appending each write reproduces sequential writing.
        PythonFileSystem.Await(_fileSystem.AppendFileAsync(_path, bytes));
        _content = null;
    }
}
