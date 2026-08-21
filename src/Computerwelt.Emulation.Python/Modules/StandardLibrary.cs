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
    public static Dictionary<string, PyObject> Create(
        VirtualMachine machine,
        TimeProvider? timeProvider = null,
        IPyFileSystem? fileSystem = null)
    {
        var modules = Core(machine, timeProvider);
        modules["pathlib"] = PathlibModule.Create(fileSystem);

        if (fileSystem is not null)
        {
            modules["os"] = OsModule.Create(fileSystem);
        }

        return modules;
    }

    private static Dictionary<string, PyObject> Core(VirtualMachine machine, TimeProvider? timeProvider) =>
        new(StringComparer.Ordinal)
    {
        ["re"] = ReModule.Create(),
        ["dataclasses"] = DataclassesModule.Create(machine),
        ["datetime"] = DatetimeModule.Create(timeProvider ?? TimeProvider.System),
        ["math"] = MathModule.Create(),
        ["sys"] = SysModule.Create(machine),
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
