using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>The <c>fnmatch</c> module.</summary>
/// <remarks>
/// <para>
/// Pure string matching, so it needs no filesystem and is importable even when the sandbox
/// has none — which also makes it the way to filter a list of names a host handed in.
/// </para>
/// <para>
/// <c>fnmatch</c> and <c>fnmatchcase</c> are the same function here. CPython separates them
/// so that a case-insensitive filesystem can fold both sides first; every path in this
/// sandbox is POSIX and case-sensitive, so there is nothing to fold.
/// </para>
/// </remarks>
public static class FnmatchModule
{
    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create()
    {
        var module = new PyModuleObject("fnmatch");

        module.Add("fnmatch", Match("fnmatch"));
        module.Add("fnmatchcase", Match("fnmatchcase"));

        module.Add("filter", new PyBuiltinFunction("filter", arguments =>
        {
            Arity.Exact("filter", arguments, 2);

            var pattern = Text(arguments[1], "filter");
            var expression = Globbing.Pattern(pattern, segment: false);

            return new PyList([
                .. (arguments[0].Iterate() ?? throw new PyRaise(PyErrors.TypeError(
                        $"'{arguments[0].TypeName}' object is not iterable")))
                    .Where(name => expression.IsMatch(Text(name, "filter")))]);
        }));

        module.Add("translate", new PyBuiltinFunction("translate", arguments =>
        {
            Arity.Exact("translate", arguments, 1);
            return new PyStr(Globbing.Translate(Text(arguments[0], "translate")));
        }));

        return module;
    }

    private static PyBuiltinFunction Match(string name) =>
        new(name, arguments =>
        {
            Arity.Exact(name, arguments, 2);

            return PyBool.Of(Globbing.Matches(
                Text(arguments[0], name),
                Text(arguments[1], name)));
        });

    private static string Text(PyObject value, string function) => value switch
    {
        PyStr text => text.Value,
        PyPath path => path.Value,
        _ => throw new PyRaise(PyErrors.TypeError(
            $"{function}() argument must be str or os.PathLike, not {value.TypeName}")),
    };
}
