using Computerwelt.Emulation.Python.Compilation;
using Computerwelt.Emulation.Python.Parsing;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python;

/// <summary>
/// A library written in Python rather than in C#.
/// </summary>
/// <remarks>
/// <para>
/// The source is compiled and run inside the sandbox, under the same limits as the program
/// that imported it, in a namespace of its own. It is not privileged: it can reach exactly
/// what a program in that sandbox can reach, so shipping a helper module this way adds
/// vocabulary without adding authority.
/// </para>
/// <para>
/// This is the natural shape for a house style guide, a set of domain helpers or a
/// compatibility shim — the things that are far easier to write as Python than to assemble
/// out of <see cref="PyObject"/>s.
/// </para>
/// </remarks>
public sealed class PythonSourceLibrary : PythonLibrary
{
    private readonly string _source;

    /// <summary>Creates a library from Python source.</summary>
    /// <param name="name">The name a program imports.</param>
    /// <param name="source">The module's source.</param>
    public PythonSourceLibrary(string name, string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(source);

        Name = name;
        _source = source;
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override PyObject Create(PythonHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var globals = new PyDict();

        // A module knows its own name, and it is not `__main__` — so a source file with the
        // usual `if __name__ == "__main__":` guard behaves as a library when imported.
        globals.Set(new PyStr("__name__"), new PyStr(Name));
        globals.Set(new PyStr("__file__"), new PyStr($"<{Name}>"));

        CodeObject code;

        try
        {
            code = Compiler.CompileModule(Parser.Parse(_source), $"<{Name}>");
        }
        catch (PythonSyntaxError error)
        {
            // A host exception must not cross into a sandboxed program, so the library's own
            // syntax error arrives as the import failure it is, catchable and with the line
            // that caused it.
            throw new PyRaise(new PyException(
                PyExceptionType.ImportError,
                $"module '{Name}' failed to compile: line {error.Line}: {error.Message}"));
        }

        // Anything the module body raises propagates to the importing program, as it does in
        // CPython — and because nothing is cached until this returns, a failed import leaves
        // no half-built module behind for the next one to find.
        context.Machine.RunModule(code, globals);

        return new PySourceModule(Name, globals);
    }
}

/// <summary>A module whose members are the globals its source left behind.</summary>
internal sealed class PySourceModule : PyObject
{
    private readonly PyDict _globals;

    public PySourceModule(string name, PyDict globals)
    {
        Name = name;
        _globals = globals;
    }

    public string Name { get; }

    public override string TypeName => "module";

    public override string Repr() => $"<module '{Name}'>";

    public override PyObject? GetAttribute(string name) =>
        _globals.TryGetValue(new PyStr(name), out var value) ? value : null;
}
