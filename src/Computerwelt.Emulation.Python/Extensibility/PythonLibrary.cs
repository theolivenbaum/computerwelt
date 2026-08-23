using Computerwelt.Emulation.Python.Modules;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python;

/// <summary>
/// A module the host adds to the importable set.
/// </summary>
/// <remarks>
/// <para>
/// A library is a <i>recipe</i>, not a module: it is asked for a module the first time a
/// run imports its name, and asked again for the next run. That is deliberate and is the
/// difference between this and putting an object in <see cref="PythonRunner.Modules"/> —
/// one module object shared by every run is one place for one tenant's data to become
/// another tenant's, and a library written in Python has module-level state by
/// construction.
/// </para>
/// <para>
/// Registration is still the whole permission model. A name that is not registered is not
/// importable; there is no search path, and nothing here reaches the host's Python.
/// </para>
/// </remarks>
public abstract class PythonLibrary
{
    /// <summary>The name a program imports.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// Builds the module for one run.
    /// </summary>
    /// <remarks>
    /// Called at most once per run, when the program first imports the name — so a library
    /// a program never mentions costs nothing. Anything it throws that is not a
    /// <see cref="PyRaise"/> is a host exception escaping into a sandboxed program, so an
    /// implementation must translate its own failures into Python exceptions.
    /// </remarks>
    public abstract PyObject Create(PythonHostContext context);

    /// <summary>Creates a library from Python source the host supplies.</summary>
    /// <param name="name">The name a program imports.</param>
    /// <param name="source">The module's source, run in a namespace of its own on first import.</param>
    public static PythonLibrary FromSource(string name, string source) => new PythonSourceLibrary(name, source);

    /// <summary>Creates a library whose module the host builds in C#.</summary>
    /// <param name="name">The name a program imports.</param>
    /// <param name="factory">Builds the module. Called once per run that imports the name.</param>
    public static PythonLibrary FromFactory(string name, Func<PythonHostContext, PyObject> factory) =>
        new DelegateLibrary(name, factory);

    /// <summary>
    /// Creates a library that is a flat set of functions, which is what most host libraries
    /// turn out to be.
    /// </summary>
    /// <param name="name">The name a program imports.</param>
    /// <param name="functions">The module's members, by the name a program calls them by.</param>
    public static PythonLibrary FromFunctions(
        string name,
        IReadOnlyDictionary<string, Func<PyObject[], PyObject>> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);

        // Copied, so that a host mutating the dictionary afterwards cannot change what an
        // already-configured session can reach.
        var members = new Dictionary<string, Func<PyObject[], PyObject>>(functions, StringComparer.Ordinal);

        return FromFactory(name, context =>
        {
            var module = context.Module();

            foreach (var (member, implementation) in members)
            {
                module.Add(member, implementation);
            }

            return module;
        });
    }

    /// <summary>
    /// Creates a library of functions that each receive the run's environment.
    /// </summary>
    /// <remarks>
    /// The shape for host code that does real work in the sandbox rather than pure
    /// computation: every function is handed the
    /// <see cref="PythonHostContext">environment</see> of the run that called it — its
    /// filesystem, working directory, environment and clock — so a module implemented in C#
    /// can do what a builtin does.
    /// </remarks>
    /// <param name="name">The name a program imports.</param>
    /// <param name="functions">The module's members, by the name a program calls them by.</param>
    public static PythonLibrary FromFunctions(
        string name,
        IReadOnlyDictionary<string, Func<PythonHostContext, PyObject[], PyObject>> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);

        var members = new Dictionary<string, Func<PythonHostContext, PyObject[], PyObject>>(
            functions, StringComparer.Ordinal);

        return FromFactory(name, context =>
        {
            var module = context.Module();

            foreach (var (member, implementation) in members)
            {
                // The context the module was built with is the run's, so each call sees the
                // environment as it is *now* — a `cd` between two calls moves both.
                module.Add(member, arguments => implementation(context, arguments));
            }

            return module;
        });
    }
}

/// <summary>A library whose module comes from a delegate.</summary>
internal sealed class DelegateLibrary : PythonLibrary
{
    private readonly Func<PythonHostContext, PyObject> _factory;

    public DelegateLibrary(string name, Func<PythonHostContext, PyObject> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);

        Name = name;
        _factory = factory;
    }

    public override string Name { get; }

    public override PyObject Create(PythonHostContext context) => _factory(context);
}
