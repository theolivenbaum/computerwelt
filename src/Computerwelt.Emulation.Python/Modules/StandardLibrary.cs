using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// The importable module set.
/// </summary>
/// <remarks>
/// This is the complete list. A module absent here cannot be imported at all — there is no
/// search path and no fallback to the host's Python, which is what makes the sandbox's
/// reach a closed, reviewable set.
/// </remarks>
public static class StandardLibrary
{
    /// <summary>Builds the standard modules for a machine.</summary>
    /// <param name="machine">The machine the modules call back into.</param>
    /// <param name="timeProvider">The clock, or null for the system one.</param>
    /// <param name="fileSystem">
    /// The filesystem <c>os</c> and <c>pathlib</c> are built over. With none, the
    /// filesystem modules are absent rather than present and always failing.
    /// </param>
    /// <param name="arguments">What <c>sys.argv</c> reports, program name first.</param>
    /// <param name="standardInput">
    /// The stream <c>sys.stdin</c> reads, or null when the program has no standard input —
    /// in which case the attribute is absent rather than an empty stream.
    /// </param>
    public static Dictionary<string, PyObject> Create(
        VirtualMachine machine,
        TimeProvider? timeProvider = null,
        IPyFileSystem? fileSystem = null,
        IReadOnlyList<string>? arguments = null,
        PyMemoryStream? standardInput = null)
    {
        var modules = Core(machine, timeProvider, arguments, standardInput);

        // One cap, read from the run's limits, shared by everything that walks a tree —
        // so `glob`, `os.walk` and `Path.walk` cannot disagree about how deep is too deep.
        var maxDepth = machine.Limits.MaxDirectoryDepth;

        modules["pathlib"] = PathlibModule.Create(fileSystem, maxDepth);
        modules["io"] = IoModule.Create(fileSystem);

        // Pattern matching over plain strings needs nothing from the host, so it is here
        // even for a sandbox with no storage — a program filtering names a host handed in
        // is a perfectly ordinary use of it.
        modules["fnmatch"] = FnmatchModule.Create();

        if (fileSystem is not null)
        {
            var os = OsModule.Create(fileSystem, machine);
            modules["os"] = os;
            modules["glob"] = GlobModule.Create(fileSystem, maxDepth);

            // `import os.path` and `import posixpath` are two more names for the object
            // `os.path` already is — one module, three ways to reach it, as CPython has it
            // on a POSIX host.
            if (os.GetAttribute("path") is { } path)
            {
                modules["os.path"] = path;
                modules["posixpath"] = path;
            }
        }

        return modules;
    }

    private static Dictionary<string, PyObject> Core(
        VirtualMachine machine,
        TimeProvider? timeProvider,
        IReadOnlyList<string>? arguments,
        PyMemoryStream? standardInput) =>
        new(StringComparer.Ordinal)
    {
        ["re"] = ReModule.Create(),
        ["dataclasses"] = DataclassesModule.Create(machine),
        ["datetime"] = DatetimeModule.Create(timeProvider ?? TimeProvider.System),
        ["math"] = MathModule.Create(),
        ["sys"] = SysModule.Create(machine, arguments, standardInput),
        ["json"] = JsonModule.Create(machine),
        ["asyncio"] = AsyncioModule.Create(machine),
        ["collections"] = SupportModules.CreateCollections(machine),
        ["itertools"] = SupportModules.CreateItertools(machine),
        ["gc"] = SupportModules.CreateGc(),
        ["unicodedata"] = UnicodedataModule.Create(),
        ["typing"] = SupportModules.CreateTyping(),
        ["__future__"] = new PyModuleObject("__future__").Add("annotations", PyNone.Instance),
    };
}
