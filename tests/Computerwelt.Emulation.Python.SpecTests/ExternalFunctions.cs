using System.Numerics;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.SpecTests;

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

        ["make_point"] = static _ => Point("Point", frozen: true),

        ["make_mutable_point"] = static _ => Point("MutablePoint", frozen: false),

        ["make_user"] = static arguments => new PyDataclass(
            "User",
            ["name", "active"],
            [arguments[0], PyBool.True],
            frozen: true)
        {
            Methods = new Dictionary<string, Func<PyDataclass, PyObject[], PyDict?, PyObject>>(StringComparer.Ordinal)
            {
                ["greeting"] = static (self, _, _) =>
                    new PyStr($"Hello, {self.GetAttribute("name")!.Display()}!"),
            },
        },

        ["make_empty"] = static _ => new PyDataclass("Empty", [], [], frozen: true),

        // The async half of the boundary: a host call that returns an awaitable. Both are
        // already settled, because a host call in this harness has nothing to wait on.
        ["async_call"] = static arguments => new PyFuture(arguments.Length > 0 ? arguments[0] : PyNone.Instance),

        ["async_fail"] = static arguments => new PyFuture(() =>
            throw new PyRaise(PyErrors.Create(arguments[0].Display(), arguments[1].Display()))),
    };

    /// <summary>
    /// A point record with the methods upstream's harness gives it.
    /// </summary>
    /// <remarks>
    /// The methods exist so the fixtures can exercise calling into a host object with each
    /// argument shape — none, one, two, and keyword-only.
    /// </remarks>
    private static PyDataclass Point(string name, bool frozen) => new(
        name,
        ["x", "y"],
        [new PyInt(1), new PyInt(2)],
        frozen)
    {
        Methods = new Dictionary<string, Func<PyDataclass, PyObject[], PyDict?, PyObject>>(StringComparer.Ordinal)
        {
            ["sum"] = static (self, _, _) => new PyInt(X(self) + Y(self)),

            ["add"] = static (self, arguments, _) => new PyDataclass(
                self.TypeName,
                ["x", "y"],
                [new PyInt(X(self) + RequireInt(arguments[0])), new PyInt(Y(self) + RequireInt(arguments[1]))],
                self.Frozen),

            ["scale"] = static (self, arguments, _) => new PyDataclass(
                self.TypeName,
                ["x", "y"],
                [new PyInt(X(self) * RequireInt(arguments[0])), new PyInt(Y(self) * RequireInt(arguments[0]))],
                self.Frozen),

            ["describe"] = static (self, arguments, keywords) =>
            {
                var label = arguments.Length > 0 ? arguments[0]
                    : keywords is not null && keywords.TryGetValue(new PyStr("label"), out var named) ? named
                    : new PyStr(self.TypeName);

                return new PyStr($"{label.Display()}({X(self)}, {Y(self)})");
            },
        },
    };

    private static BigInteger X(PyDataclass point) => RequireInt(point.GetAttribute("x")!);

    private static BigInteger Y(PyDataclass point) => RequireInt(point.GetAttribute("y")!);

    private static BigInteger RequireInt(PyObject value) => value switch
    {
        PyInt integer => integer.Value,
        _ => throw new PyRaise(PyErrors.TypeError($"expected int, got {value.TypeName}")),
    };

    /// <summary>
    /// The non-function names upstream's harness resolves, as plain values.
    /// </summary>
    public static Dictionary<string, PyObject> Constants() => new(StringComparer.Ordinal)
    {
        ["CONST_INT"] = new PyInt(42),
        ["CONST_STR"] = new PyStr("hello"),
        ["CONST_FLOAT"] = new PyFloat(3.14),
        ["CONST_BOOL"] = PyBool.True,
        ["CONST_LIST"] = new PyList([new PyInt(1), new PyInt(2), new PyInt(3)]),
        ["CONST_NONE"] = PyNone.Instance,
    };
}
