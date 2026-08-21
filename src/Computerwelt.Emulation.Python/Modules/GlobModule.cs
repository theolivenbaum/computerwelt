using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>The <c>glob</c> module.</summary>
/// <remarks>
/// <para>
/// Present only when the sandbox has a filesystem, for the same reason <c>os</c> is: a
/// <c>glob</c> that always returns nothing would look like an empty directory rather than
/// like the absence of storage.
/// </para>
/// <para>
/// <c>iglob</c> returns a list, not a generator. The distinction exists in CPython so that
/// a pattern over an enormous tree can be abandoned part-way; here the walk is over a
/// virtual filesystem inside a bounded instruction budget, and the laziness would buy a
/// caller nothing it cannot get from slicing.
/// </para>
/// </remarks>
public static class GlobModule
{
    /// <summary>Builds the module over <paramref name="fileSystem"/>.</summary>
    public static PyModuleObject Create(IPyFileSystem fileSystem)
    {
        var module = new PyModuleObject("glob");

        module.Add("glob", Expand("glob", fileSystem));
        module.Add("iglob", Expand("iglob", fileSystem));

        module.Add("escape", new PyBuiltinFunction("escape", arguments =>
        {
            Arity.Exact("escape", arguments, 1);
            return new PyStr(Globbing.Escape(Text(arguments[0], "escape")));
        }));

        module.Add("has_magic", new PyBuiltinFunction("has_magic", arguments =>
        {
            Arity.Exact("has_magic", arguments, 1);
            return PyBool.Of(Globbing.HasMagic(Text(arguments[0], "has_magic")));
        }));

        return module;
    }

    private static PyBuiltinFunction Expand(string name, IPyFileSystem fileSystem) =>
        new(name, (arguments, keywords) =>
        {
            if (arguments.Length is 0 or > 2)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{name}() takes 1 positional argument but {arguments.Length} were given"));
            }

            var pattern = Text(arguments[0], name);
            var root = fileSystem.WorkingDirectory;
            var recursive = arguments.Length > 1 && arguments[1].IsTruthy();
            var includeHidden = false;

            foreach (var (key, value) in keywords?.Entries ?? [])
            {
                switch (key.Display())
                {
                    case "root_dir":
                        root = value is PyNone ? fileSystem.WorkingDirectory : Text(value, name);
                        break;

                    case "recursive":
                        recursive = value.IsTruthy();
                        break;

                    case "include_hidden":
                        includeHidden = value.IsTruthy();
                        break;

                    // A directory descriptor names something this sandbox has no equivalent
                    // of, so it is refused rather than quietly ignored.
                    case "dir_fd" when value is not PyNone:
                        throw new PyRaise(PyErrors.TypeError(
                            $"{name}: dir_fd is not supported on this platform"));

                    case "dir_fd":
                        break;

                    default:
                        throw new PyRaise(PyErrors.TypeError(
                            $"{name}() got an unexpected keyword argument '{key.Display()}'"));
                }
            }

            // A relative root is resolved before the walk, so results stay relative to the
            // root the caller named rather than to the working directory.
            if (!root.StartsWith('/'))
            {
                root = Globbing.Join(fileSystem.WorkingDirectory, root);
            }

            return new PyList([
                .. Globbing.Expand(fileSystem, root, pattern, recursive, includeHidden)
                    .Select(static path => (PyObject)new PyStr(path))]);
        });

    private static string Text(PyObject value, string function) => value switch
    {
        PyStr text => text.Value,
        PyPath path => path.Value,
        _ => throw new PyRaise(PyErrors.TypeError(
            $"{function}() argument must be str or os.PathLike, not {value.TypeName}")),
    };
}
