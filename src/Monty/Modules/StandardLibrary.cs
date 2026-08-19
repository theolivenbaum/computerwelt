using Monty.Runtime;

namespace Monty.Modules;

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
    public static Dictionary<string, PyObject> Create(VirtualMachine machine, TimeProvider? timeProvider = null) =>
        new(StringComparer.Ordinal)
    {
        ["re"] = ReModule.Create(),
        ["dataclasses"] = DataclassesModule.Create(machine),
        ["datetime"] = DatetimeModule.Create(timeProvider ?? TimeProvider.System),
        ["math"] = MathModule.Create(),
        ["sys"] = SysModule.Create(machine),
        ["json"] = JsonModule.Create(),
        ["collections"] = SupportModules.CreateCollections(machine),
        ["itertools"] = SupportModules.CreateItertools(machine),
        ["typing"] = SupportModules.CreateTyping(),
        ["__future__"] = new PyModuleObject("__future__").Add("annotations", PyNone.Instance),
    };
}
