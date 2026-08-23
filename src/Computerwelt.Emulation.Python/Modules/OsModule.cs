using System.Numerics;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

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
    /// <param name="fileSystem">The storage the module works over.</param>
    /// <param name="machine">
    /// The machine a callback is invoked through — <c>os.walk</c>'s <c>onerror</c> is the
    /// only one. Without it an unreadable directory is skipped silently, which is what
    /// <c>os.walk</c> does when no handler was given anyway.
    /// </param>
    public static PyModuleObject Create(IPyFileSystem fileSystem, VirtualMachine? machine = null)
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
            var given = Clinic("fspath", ["path"], arguments, keywords, maxPositional: 1, required: 1);

            return given[0] switch
            {
                PyStr or PyBytes => given[0]!,
                PyPath path => new PyStr(path.Value),
                PyDirEntry entry => new PyStr(entry.Path),
                _ => throw new PyRaise(PyErrors.TypeError(
                    $"expected str, bytes or os.PathLike object, not {given[0]!.TypeName}")),
            };
        }));

        module.Add("getcwd", new PyBuiltinFunction("getcwd", (arguments, keywords) =>
        {
            _ = Clinic("getcwd", [], arguments, keywords, maxPositional: 0, required: 0);
            return new PyStr(fileSystem.WorkingDirectory);
        }));

        module.Add("listdir", new PyBuiltinFunction("listdir", (arguments, keywords) =>
        {
            var given = Clinic("listdir", ["path"], arguments, keywords, maxPositional: 1, required: 0);

            // A None path means the working directory, which is why this converter mentions
            // it where the others do not.
            var target = given[0] is null or PyNone
                ? fileSystem.WorkingDirectory
                : Located(given[0]!, "listdir", "path", "string, bytes, os.PathLike, integer or None");

            return new PyList(
                [.. fileSystem.List(target)
                    .OrderBy(static name => name, StringComparer.Ordinal)
                    .Select(static name => (PyObject)new PyStr(name))]);
        }));

        module.Add("stat", new PyBuiltinFunction("stat", (arguments, keywords) =>
        {
            var given = Clinic(
                "stat",
                ["path", "dir_fd", "follow_symlinks"],
                arguments,
                keywords,
                maxPositional: 1,
                required: 1);

            var target = Located(given[0]!, "stat", "path", "string, bytes, os.PathLike or integer");
            Descriptor(given[1]);

            return new PyStat(
                fileSystem.Mode(target),
                fileSystem.IsDirectory(target) ? 0 : fileSystem.Size(target),
                fileSystem.ModifiedAt(target));
        }));

        module.Add("mkdir", new PyBuiltinFunction("mkdir", (arguments, keywords) =>
        {
            var given = Clinic(
                "mkdir",
                ["path", "mode", "dir_fd"],
                arguments,
                keywords,
                maxPositional: 2,
                required: 1,
                exactPositional: false);

            var target = Located(given[0]!, "mkdir", "path", "string, bytes or os.PathLike");
            Permissions(given[1]);
            Descriptor(given[2]);

            fileSystem.CreateDirectory(target, parents: false, existsOk: false);
            return PyNone.Instance;
        }));

        // `makedirs` is written in Python upstream rather than in C, so its arity and
        // converter complaints are the interpreter's own rather than the clinic's.
        module.Add("makedirs", new PyBuiltinFunction("makedirs", (arguments, keywords) =>
        {
            string[] names = ["name", "mode", "exist_ok"];

            if (arguments.Length > names.Length)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"makedirs() takes from 1 to {names.Length} positional arguments but {arguments.Length} were given"));
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
                        $"makedirs() got an unexpected keyword argument '{key.Display()}'"));
                }

                given[position] = value;
            }

            if (given[0] is null)
            {
                throw new PyRaise(PyErrors.TypeError(
                    "makedirs() missing 1 required positional argument: 'name'"));
            }

            var target = Text(given[0]!);
            Permissions(given[1]);

            fileSystem.CreateDirectory(target, parents: true, existsOk: given[2]?.IsTruthy() ?? false);
            return PyNone.Instance;
        }));

        foreach (var name in new[] { "remove", "unlink" })
        {
            module.Add(name, new PyBuiltinFunction(name, (arguments, keywords) =>
            {
                var given = Clinic(name, ["path", "dir_fd"], arguments, keywords, maxPositional: 1, required: 1);
                var target = Located(given[0]!, name, "path", "string, bytes or os.PathLike");
                Descriptor(given[1]);

                fileSystem.Remove(target);
                return PyNone.Instance;
            }));
        }

        module.Add("rmdir", new PyBuiltinFunction("rmdir", (arguments, keywords) =>
        {
            var given = Clinic("rmdir", ["path", "dir_fd"], arguments, keywords, maxPositional: 1, required: 1);
            var target = Located(given[0]!, "rmdir", "path", "string, bytes or os.PathLike");
            Descriptor(given[1]);

            fileSystem.RemoveDirectory(target);
            return PyNone.Instance;
        }));

        // `replace` overwrites where `rename` refuses to; the virtual filesystem's rename
        // already does, so the two share one implementation.
        foreach (var name in new[] { "rename", "replace" })
        {
            module.Add(name, new PyBuiltinFunction(name, (arguments, keywords) =>
            {
                var given = Clinic(
                    name,
                    ["src", "dst", "src_dir_fd", "dst_dir_fd"],
                    arguments,
                    keywords,
                    maxPositional: 2,
                    required: 2);

                var from = Located(given[0]!, name, "src", "string, bytes or os.PathLike");
                var to = Located(given[1]!, name, "dst", "string, bytes or os.PathLike");
                Descriptor(given[2]);
                Descriptor(given[3]);

                fileSystem.Rename(from, to);
                return PyNone.Instance;
            }));
        }

        module.Add("walk", new PyBuiltinFunction("walk", (arguments, keywords) =>
        {
            var given = Clinic(
                "walk",
                ["top", "topdown", "onerror", "followlinks", "max_depth"],
                arguments,
                keywords,
                maxPositional: 4,
                required: 1,
                exactPositional: false);

            var top = Located(given[0]!, "walk", "top", "string, bytes or os.PathLike");
            var topDown = given[1] is null || given[1]!.IsTruthy();
            var onError = given[2] is null or PyNone ? null : given[2];

            // `followlinks` is accepted and has nothing to do: this filesystem has no
            // symbolic links, so a walk cannot loop through one.
            _ = given[3];

            return new PyIterator(
                Walk(fileSystem, top, topDown, onError, machine, Depth(given[4]), DepthCap(machine)),
                "generator");
        }));

        module.Add("scandir", new PyBuiltinFunction("scandir", (arguments, keywords) =>
        {
            var given = Clinic("scandir", ["path"], arguments, keywords, maxPositional: 1, required: 0);

            var target = given[0] is null or PyNone
                ? fileSystem.WorkingDirectory
                : Located(given[0]!, "scandir", "path", "string, bytes, os.PathLike, integer or None");

            return new PyScandir(target, [
                .. fileSystem.List(target)
                    .OrderBy(static name => name, StringComparer.Ordinal)
                    .Select(name => new PyDirEntry(name, PyPath.Join(target, name), fileSystem))]);
        }));

        module.Add("path", CreatePath(fileSystem));
        return module;
    }

    /// <summary>
    /// Produces <c>os.walk</c>'s <c>(dirpath, dirnames, filenames)</c> triples, lazily.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Iterative and lazy, both deliberately. Iterative because recursion would spend the
    /// host's stack on the depth of a tree the program chose, and a sandbox must not let a
    /// program decide how much host stack to use. Lazy because a top-down walk is supposed
    /// to honour edits to the directory list it just handed out —
    /// <c>dirnames[:] = [d for d in dirnames if d != 'obj']</c> is how a walk prunes a
    /// subtree, and it only works if the descent happens after the caller's turn.
    /// </para>
    /// <para>
    /// A bottom-up walk cannot be pruned that way in CPython either: the children are
    /// already visited by the time the parent is reported.
    /// </para>
    /// </remarks>
    /// <param name="fileSystem">Where to walk.</param>
    /// <param name="top">The directory to start from.</param>
    /// <param name="topDown">True to report a directory before its children.</param>
    /// <param name="onError">A callable handed the <c>OSError</c> from an unreadable directory.</param>
    /// <param name="machine">The machine <paramref name="onError"/> is called through.</param>
    /// <param name="maxDepth">
    /// How many levels below <paramref name="top"/> to descend, or null for no bound of the
    /// caller's own. Unlike <paramref name="depthCap"/> this <i>stops</i> rather than
    /// raising: it is the caller asking for less, not a limit being hit.
    /// </param>
    /// <param name="depthCap">
    /// The sandbox's own cap on absolute path depth. Reaching it raises, because a walk
    /// that quietly stopped part-way would report a subset of the tree as though it were
    /// all of it.
    /// </param>
    internal static IEnumerable<PyObject> Walk(
        IPyFileSystem fileSystem,
        string top,
        bool topDown,
        PyObject? onError,
        VirtualMachine? machine,
        int? maxDepth = null,
        int depthCap = Globbing.DefaultMaxDepth)
    {
        // Depth is counted from `top`, so `max_depth=1` means "this directory and the ones
        // directly under it" whatever the absolute depth of the starting point.
        var origin = Globbing.Depth(top);

        if (topDown)
        {
            var pending = new List<string> { top };

            while (pending.Count > 0)
            {
                var directory = pending[0];
                pending.RemoveAt(0);

                var (directories, files) = Split(fileSystem, directory, onError, machine);

                if (directories is null)
                {
                    continue;
                }

                yield return new PyTuple([new PyStr(directory), directories, files!]);

                if (maxDepth is { } limit && Globbing.Depth(directory) - origin >= limit)
                {
                    continue;
                }

                Globbing.EnsureDepth(directory, depthCap);

                // Read back from the very list object the caller was given, after its turn
                // — anything it removed is never descended into.
                pending.InsertRange(0, directories.Items.Select(name => PyPath.Join(directory, name.Display())));
            }

            yield break;
        }

        foreach (var triple in BottomUp(fileSystem, top, onError, machine, maxDepth, depthCap, origin))
        {
            yield return triple;
        }
    }

    /// <summary>Reads the <c>max_depth</c> argument, which must be a non-negative integer.</summary>
    private static int? Depth(PyObject? value) => value switch
    {
        null or PyNone => null,
        PyInt { Value: var depth } when depth >= 0 => (int)depth,
        PyInt => throw new PyRaise(PyErrors.ValueError("max_depth must not be negative")),
        _ => throw new PyRaise(PyErrors.TypeError(
            $"'{value.TypeName}' object cannot be interpreted as an integer")),
    };

    /// <summary>The sandbox's depth cap, from the machine's limits when there is one.</summary>
    private static int DepthCap(VirtualMachine? machine) =>
        machine?.Limits.MaxDirectoryDepth ?? Globbing.DefaultMaxDepth;

    /// <summary>Walks a subtree children-first.</summary>
    /// <remarks>
    /// The recursion here is over the host's stack, so it is bounded by the same instruction
    /// budget the rest of a run is: a tree deep enough to overflow it costs more listings
    /// than a program is allowed to make first.
    /// </remarks>
    private static IEnumerable<PyObject> BottomUp(
        IPyFileSystem fileSystem,
        string directory,
        PyObject? onError,
        VirtualMachine? machine,
        int? maxDepth,
        int depthCap,
        int origin)
    {
        var (directories, files) = Split(fileSystem, directory, onError, machine);

        if (directories is null)
        {
            yield break;
        }

        // Bottom-up cannot be pruned by editing the list — the children are already
        // visited by the time the parent is reported — so both bounds are checked before
        // the recursion rather than after the yield.
        if (maxDepth is not { } limit || Globbing.Depth(directory) - origin < limit)
        {
            Globbing.EnsureDepth(directory, depthCap);

            foreach (var name in directories.Items.ToList())
            {
                var child = PyPath.Join(directory, name.Display());

                foreach (var triple in BottomUp(fileSystem, child, onError, machine, maxDepth, depthCap, origin))
                {
                    yield return triple;
                }
            }
        }

        yield return new PyTuple([new PyStr(directory), directories, files!]);
    }

    /// <summary>
    /// Lists a directory into its subdirectories and its files, or reports it unreadable.
    /// </summary>
    /// <returns>Two lists, or two nulls when the directory could not be read.</returns>
    private static (PyList? Directories, PyList? Files) Split(
        IPyFileSystem fileSystem,
        string directory,
        PyObject? onError,
        VirtualMachine? machine)
    {
        IReadOnlyList<string> names;

        try
        {
            names = fileSystem.List(directory);
        }
        catch (PyRaise raise)
        {
            // An unreadable directory is skipped silently unless the caller asked to hear
            // about it, which is what `onerror` is for.
            if (onError is not null && machine is not null)
            {
                machine.Call(onError, [raise.Exception]);
            }

            return (null, null);
        }

        var directories = new List<PyObject>();
        var files = new List<PyObject>();

        foreach (var name in names.OrderBy(static name => name, StringComparer.Ordinal))
        {
            (fileSystem.IsDirectory(PyPath.Join(directory, name)) ? directories : files).Add(new PyStr(name));
        }

        return (new PyList(directories), new PyList(files));
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

        Add(path, "abspath", 1, 1, (arguments, _) => new PyStr(Absolute(Text(arguments[0]), fileSystem)));
        Add(path, "normpath", 1, 1, (arguments, _) => new PyStr(Normalize(Text(arguments[0]))));
        Add(path, "isabs", 1, 1, (arguments, _) => PyBool.Of(Text(arguments[0]).StartsWith('/')));
        Add(path, "exists", 1, 1, (arguments, _) => PyBool.Of(fileSystem.Exists(Text(arguments[0]))));
        Add(path, "isfile", 1, 1, (arguments, _) => PyBool.Of(fileSystem.IsFile(Text(arguments[0]))));
        Add(path, "isdir", 1, 1, (arguments, _) => PyBool.Of(fileSystem.IsDirectory(Text(arguments[0]))));
        Add(path, "islink", 1, 1, (_, _) => PyBool.False);
        Add(path, "lexists", 1, 1, (arguments, _) => PyBool.Of(fileSystem.Exists(Text(arguments[0]))));
        Add(path, "getsize", 1, 1, (arguments, _) => PyInt.From(fileSystem.Size(Text(arguments[0]))));
        Add(path, "getmtime", 1, 1, (arguments, _) => new PyFloat(fileSystem.ModifiedAt(Text(arguments[0]))));

        // No filesystem here has symbolic links, so resolving is normalising.
        Add(path, "realpath", 1, 1, (arguments, _) => new PyStr(Absolute(Text(arguments[0]), fileSystem)));

        // Paths are POSIX whatever the host is, so case folding would be wrong even on
        // Windows: `normcase` is the identity.
        Add(path, "normcase", 1, 1, (arguments, _) => new PyStr(Text(arguments[0])));

        Add(path, "relpath", 1, 2, (arguments, keywords) =>
        {
            var start = arguments.Length > 1 ? Text(arguments[1])
                : keywords?.TryGetValue(new PyStr("start"), out var named) == true ? Text(named)
                : ".";

            return new PyStr(Relative(Absolute(Text(arguments[0]), fileSystem), Absolute(start, fileSystem)));
        });

        Add(path, "commonprefix", 1, 1, (arguments, _) =>
        {
            var values = (arguments[0].Iterate() ?? throw new PyRaise(PyErrors.TypeError(
                $"'{arguments[0].TypeName}' object is not iterable")))
                .Select(Text).ToList();

            if (values.Count == 0)
            {
                return new PyStr(string.Empty);
            }

            // Character-wise, not component-wise — the documented wart, and scripts that
            // want the sane one call commonpath.
            var shortest = values.Min(static value => value.Length);
            var length = 0;

            while (length < shortest && values.All(value => value[length] == values[0][length]))
            {
                length++;
            }

            return new PyStr(values[0][..length]);
        });

        Add(path, "commonpath", 1, 1, (arguments, _) =>
        {
            var values = (arguments[0].Iterate() ?? throw new PyRaise(PyErrors.TypeError(
                $"'{arguments[0].TypeName}' object is not iterable")))
                .Select(Text).ToList();

            if (values.Count == 0)
            {
                throw new PyRaise(PyErrors.ValueError("commonpath() arg is an empty sequence"));
            }

            var absolute = values[0].StartsWith('/');

            if (values.Any(value => value.StartsWith('/') != absolute))
            {
                throw new PyRaise(PyErrors.ValueError("Can't mix absolute and relative paths"));
            }

            var parts = values
                .Select(static value => value.Split('/', StringSplitOptions.RemoveEmptyEntries))
                .ToList();

            var shared = new List<string>();

            for (var i = 0; i < parts.Min(static p => p.Length); i++)
            {
                if (parts.Any(p => !string.Equals(p[i], parts[0][i], StringComparison.Ordinal)))
                {
                    break;
                }

                shared.Add(parts[0][i]);
            }

            var joined = string.Join('/', shared);
            return new PyStr(absolute ? "/" + joined : joined);
        });

        // There are no home directories in this sandbox, so `~` stays as written rather
        // than expanding to somewhere that does not exist.
        Add(path, "expanduser", 1, 1, (arguments, _) => new PyStr(Text(arguments[0])));

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
        // `newline=''` is refused along with the rest, even though nothing here translates
        // line endings and it would therefore describe what already happens: upstream's
        // corpus pins the rejection, and a caller passing it is asking for a guarantee
        // about newline handling that this wrapper does not make.
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

        // A DirEntry is os.PathLike, which is what lets `open(entry)` work directly on
        // what a scan produced.
        PyDirEntry entry => entry.Path,
        PyBytes bytes => Encoding.UTF8.GetString(bytes.Value),
        _ => throw new PyRaise(PyErrors.TypeError(
            $"expected str, bytes or os.PathLike object, not {value.TypeName}")),
    };

    /// <summary>Resolves a path against the working directory, without touching storage.</summary>
    private static string Absolute(string value, IPyFileSystem fileSystem) =>
        Normalize(value.StartsWith('/') ? value : PyPath.Join(fileSystem.WorkingDirectory, value));

    /// <summary>
    /// Collapses <c>.</c> and <c>..</c> the way <c>os.path.normpath</c> does.
    /// </summary>
    /// <remarks>
    /// Not <see cref="PyPath.Normalize"/>, which leaves <c>..</c> in place — and correctly
    /// so, because <c>PurePath</c> is lexical about a component that could name a symbolic
    /// link. <c>os.path</c> made the opposite choice, and <c>relpath</c>, <c>commonpath</c>
    /// and <c>realpath</c> all depend on it having done so.
    /// </remarks>
    private static string Normalize(string value)
    {
        if (value.Length == 0)
        {
            return ".";
        }

        var absolute = value.StartsWith('/');
        var parts = new List<string>();

        foreach (var part in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case ".":
                    break;

                // `..` above the root is the root; above a relative path's start it has to
                // stay, because there is no way to know what it would climb to.
                case ".." when parts.Count > 0 && parts[^1] != "..":
                    parts.RemoveAt(parts.Count - 1);
                    break;

                case ".." when absolute:
                    break;

                default:
                    parts.Add(part);
                    break;
            }
        }

        var joined = string.Join('/', parts);
        return absolute ? "/" + joined : joined.Length == 0 ? "." : joined;
    }

    /// <summary>Expresses <paramref name="path"/> as a route from <paramref name="start"/>.</summary>
    /// <remarks>
    /// Purely lexical, as CPython's is: it climbs with <c>..</c> as far as it needs to and
    /// never asks the filesystem whether any of it exists.
    /// </remarks>
    private static string Relative(string path, string start)
    {
        var target = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var from = start.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var shared = 0;

        while (shared < target.Length && shared < from.Length
               && string.Equals(target[shared], from[shared], StringComparison.Ordinal))
        {
            shared++;
        }

        var steps = Enumerable.Repeat("..", from.Length - shared).Concat(target[shared..]).ToList();

        // A path that is the start itself is ".", not the empty string.
        return steps.Count == 0 ? "." : string.Join('/', steps);
    }

    /// <summary>
    /// Binds an <c>os</c> function's arguments the way CPython's argument clinic does.
    /// </summary>
    /// <remarks>
    /// The wording of every complaint here is the clinic's, down to which count the number
    /// in parentheses refers to and which check fires first — scripts match on these, and
    /// the order is not the obvious one: the total count is checked before the positional
    /// count, and a missing required argument is reported before an unknown keyword.
    /// </remarks>
    /// <param name="name">The function's name.</param>
    /// <param name="names">Every parameter name, in order.</param>
    /// <param name="arguments">The positional arguments.</param>
    /// <param name="keywords">The keyword arguments, if any.</param>
    /// <param name="maxPositional">How many parameters may be given positionally.</param>
    /// <param name="required">How many leading parameters must be supplied.</param>
    /// <param name="exactPositional">
    /// True when the positional count is fixed, which words the complaint "exactly" rather
    /// than "at most".
    /// </param>
    /// <returns>One slot per name, null where nothing was given.</returns>
    private static PyObject?[] Clinic(
        string name,
        string[] names,
        PyObject[] arguments,
        PyDict? keywords,
        int maxPositional,
        int required,
        bool exactPositional = true)
    {
        var total = arguments.Length + (keywords?.Count ?? 0);

        if (total > names.Length)
        {
            // With nothing positional the count being complained about is of keywords.
            throw new PyRaise(PyErrors.TypeError(arguments.Length == 0
                ? $"{name}() takes at most {names.Length} keyword argument{Plural(names.Length)} ({total} given)"
                : $"{name}() takes at most {names.Length} argument{Plural(names.Length)} ({total} given)"));
        }

        if (arguments.Length > maxPositional)
        {
            throw new PyRaise(PyErrors.TypeError(exactPositional
                ? $"{name}() takes exactly {maxPositional} positional argument{Plural(maxPositional)} ({arguments.Length} given)"
                : $"{name}() takes at most {maxPositional} positional argument{Plural(maxPositional)} ({arguments.Length} given)"));
        }

        var given = new PyObject?[names.Length];

        for (var i = 0; i < arguments.Length; i++)
        {
            given[i] = arguments[i];
        }

        string? unknown = null;

        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            var position = Array.IndexOf(names, key.Display());

            if (position < 0)
            {
                unknown ??= key.Display();
                continue;
            }

            if (given[position] is not null)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"argument for {name}() given by name ('{names[position]}') and position ({position + 1})"));
            }

            given[position] = value;
        }

        // A missing required argument is reported before an unknown keyword.
        for (var i = 0; i < required; i++)
        {
            if (given[i] is null)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{name}() missing required argument '{names[i]}' (pos {i + 1})"));
            }
        }

        return unknown is null
            ? given
            : throw new PyRaise(PyErrors.TypeError(
                $"{name}() got an unexpected keyword argument '{unknown}'"));
    }

    private static string Plural(int count) => count == 1 ? string.Empty : "s";

    /// <summary>Reads a path argument, wording its refusal the way the named function does.</summary>
    private static string Located(PyObject value, string function, string parameter, string kinds) =>
        value switch
        {
            PyStr text => text.Value,
            PyPath path => path.Value,
            PyDirEntry entry => entry.Path,
            PyBytes bytes => Encoding.UTF8.GetString(bytes.Value),
            _ => throw new PyRaise(PyErrors.TypeError(
                $"{function}: {parameter} should be {kinds}, not {value.TypeName}")),
        };

    /// <summary>Checks a directory-descriptor argument, which this sandbox only accepts unset.</summary>
    private static void Descriptor(PyObject? value)
    {
        if (value is null or PyNone)
        {
            return;
        }

        // The value goes through a C int before anything looks at it, so an out-of-range
        // one is an overflow rather than a bad descriptor.
        if (value is not PyInt number)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"argument should be integer or None, not {value.TypeName}"));
        }

        if (number.Value > int.MaxValue)
        {
            throw new PyRaise(new PyException(PyExceptionType.OverflowError, "fd is greater than maximum"));
        }

        if (number.Value < int.MinValue)
        {
            throw new PyRaise(new PyException(PyExceptionType.OverflowError, "fd is less than minimum"));
        }

        throw new PyRaise(new PyException(
            PyExceptionType.NotImplementedError, "dir_fd unavailable on this platform"));
    }

    /// <summary>Checks a mode argument, which is converted through a C int before use.</summary>
    private static void Permissions(PyObject? value)
    {
        switch (value)
        {
            case null or PyNone:
                return;

            case PyInt number when number.Value >= int.MinValue && number.Value <= int.MaxValue:
                return;

            case PyInt:
                throw new PyRaise(new PyException(
                    PyExceptionType.OverflowError, "Python int too large to convert to C int"));

            default:
                throw new PyRaise(PyErrors.TypeError(
                    $"'{value.TypeName}' object cannot be interpreted as an integer"));
        }
    }

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
            return PyInt.From(_position);
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

            return PyInt.From(written);
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
