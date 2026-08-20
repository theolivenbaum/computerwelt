using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>A module: a named namespace of values the host has chosen to expose.</summary>
/// <remarks>
/// Modules are built here rather than parsed from Python source, so the set of importable
/// names is a closed, auditable list. A script cannot reach anything that is not in it.
/// </remarks>
public sealed class PyModuleObject : PyObject
{
    private readonly Dictionary<string, PyObject> _members = new(StringComparer.Ordinal);

    /// <summary>Creates an empty module.</summary>
    public PyModuleObject(string name) => Name = name;

    /// <summary>The module's name.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public override string TypeName => "module";

    /// <inheritdoc />
    public override string Repr() => $"<module '{Name}'>";

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) =>
        name == "__name__" ? new PyStr(Name) : _members.GetValueOrDefault(name);

    /// <summary>Adds a member.</summary>
    public PyModuleObject Add(string name, PyObject value)
    {
        _members[name] = value;
        return this;
    }

    /// <summary>Adds a function taking positional arguments.</summary>
    public PyModuleObject Add(string name, Func<PyObject[], PyObject> implementation) =>
        Add(name, new PyBuiltinFunction($"{Name}.{name}", implementation));

    /// <summary>Adds a function taking positional and keyword arguments.</summary>
    public PyModuleObject Add(string name, Func<PyObject[], PyDict?, PyObject> implementation) =>
        Add(name, new PyBuiltinFunction($"{Name}.{name}", implementation));
}
