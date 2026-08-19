using System.Numerics;
using Monty.Runtime;

namespace Monty.SpecTests;

/// <summary>
/// The external functions upstream's own harness exposes to fixtures marked
/// <c># call-external</c>.
/// </summary>
/// <remarks>
/// These mirror <c>dispatch_external_call</c> in <c>crates/monty-datatest/src/main.rs</c>
/// exactly, because the fixtures assert on their results. They are test scaffolding, not
/// part of the library: the sandbox exposes nothing by default.
/// </remarks>
public static class ExternalFunctions
{
    /// <summary>Builds the harness's external function set.</summary>
    public static Dictionary<string, Func<PyObject[], PyObject>> Create() => new(StringComparer.Ordinal)
    {
        ["add_ints"] = static arguments => new PyInt(RequireInt(arguments[0]) + RequireInt(arguments[1])),

        ["concat_strings"] = static arguments =>
            new PyStr(arguments[0].Display() + arguments[1].Display()),

        ["return_value"] = static arguments => arguments[0],

        ["get_list"] = static _ => new PyList([new PyInt(1), new PyInt(2), new PyInt(3)]),

        ["raise_error"] = static arguments =>
            throw new PyRaise(PyErrors.Create(arguments[0].Display(), arguments[1].Display())),

        ["make_point"] = static _ =>
            new PyDataclass("Point", ["x", "y"], [new PyInt(1), new PyInt(2)], frozen: true),

        ["make_mutable_point"] = static _ =>
            new PyDataclass("MutablePoint", ["x", "y"], [new PyInt(1), new PyInt(2)], frozen: false),

        ["make_user"] = static arguments =>
            new PyDataclass("User", ["name", "active"], [arguments[0], PyBool.True], frozen: true),

        ["make_empty"] = static _ => new PyDataclass("Empty", [], [], frozen: true),
    };

    private static BigInteger RequireInt(PyObject value) => value switch
    {
        PyInt integer => integer.Value,
        _ => throw new PyRaise(PyErrors.TypeError($"expected int, got {value.TypeName}")),
    };
}
