namespace Monty.Runtime;

/// <summary>
/// Attribute lookup, which for built-in types means finding a method.
/// </summary>
/// <remarks>
/// Methods on built-in types are produced on demand as <see cref="PyBoundMethod"/> values
/// rather than stored in per-type dictionaries. That keeps every type's method table in one
/// readable switch and avoids allocating hundreds of objects at startup for methods a given
/// script will never call.
/// </remarks>
public static class Attributes
{
    /// <summary>Reads an attribute, raising <c>AttributeError</c> when it does not exist.</summary>
    public static PyObject Get(VirtualMachine machine, PyObject target, string name)
    {
        // A user-defined object answers for itself first.
        if (target.GetAttribute(name) is { } own)
        {
            return own;
        }

        if (BuiltinMethods.TryBind(machine, target, name) is { } method)
        {
            return method;
        }

        throw new PyRaise(PyErrors.AttributeError(target.TypeName, name));
    }

    /// <summary>Reads an attribute, returning null instead of raising.</summary>
    public static PyObject? TryGet(VirtualMachine machine, PyObject target, string name) =>
        target.GetAttribute(name) ?? BuiltinMethods.TryBind(machine, target, name);
}
