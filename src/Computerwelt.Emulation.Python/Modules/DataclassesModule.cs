using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>The <c>dataclasses</c> module.</summary>
/// <remarks>
/// <c>@dataclass</c> is a decorator that rewrites a class, and the class already exists as
/// a <see cref="PyClass"/> by the time the decorator runs — so the transformation happens
/// here at run time rather than in the compiler, exactly as CPython does it.
/// </remarks>
public static class DataclassesModule
{
    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create(VirtualMachine machine)
    {
        var module = new PyModuleObject("dataclasses");

        module.Add("dataclass", new PyBuiltinFunction("dataclass", (arguments, keywords) =>
        {
            // `@dataclass` and `@dataclass(frozen=True)` are both valid, so a call with no
            // class argument returns a decorator rather than a class.
            var frozen = keywords is not null
                && keywords.TryGetValue(new PyStr("frozen"), out var flag)
                && flag.IsTruthy();

            var eq = keywords is null
                || !keywords.TryGetValue(new PyStr("eq"), out var wantsEq)
                || wantsEq.IsTruthy();

            var order = keywords is not null
                && keywords.TryGetValue(new PyStr("order"), out var wantsOrder)
                && wantsOrder.IsTruthy();

            if (arguments.Length == 0)
            {
                return new PyBuiltinFunction("dataclass", inner => Decorate(machine, inner[0], frozen, eq, order));
            }

            return Decorate(machine, arguments[0], frozen, eq, order);
        }));

        module.Add("field", new PyBuiltinFunction("field", static (_, keywords) =>
            keywords is not null && keywords.TryGetValue(new PyStr("default"), out var value)
                ? value
                : PyNone.Instance));

        module.Add("is_dataclass", static arguments =>
            PyBool.Of(arguments[0] switch
            {
                PyDataclass => true,
                PyClass type => type.GetAttribute("__dataclass_fields__") is not null,

                // An instance of a dataclass is one too, which is what makes the usual
                // `is_dataclass(obj) and not isinstance(obj, type)` idiom work.
                PyInstance instance => instance.Class.GetAttribute("__dataclass_fields__") is not null,
                _ => false,
            }));

        module.Add("asdict", static arguments =>
        {
            var dict = new PyDict();

            if (arguments[0] is PyDataclass dataclass)
            {
                foreach (var field in dataclass.FieldNames)
                {
                    dict.Set(new PyStr(field), dataclass.GetAttribute(field) ?? PyNone.Instance);
                }
            }
            else if (arguments[0] is PyInstance instance)
            {
                foreach (var (key, value) in instance.Fields.Entries)
                {
                    dict.Set(key, value);
                }
            }

            return dict;
        });

        module.Add("astuple", static arguments =>
        {
            if (arguments[0] is PyDataclass dataclass)
            {
                return new PyTuple([.. dataclass.FieldNames.Select(field =>
                    dataclass.GetAttribute(field) ?? PyNone.Instance)]);
            }

            return arguments[0] is PyInstance instance
                ? new PyTuple([.. instance.Fields.Entries.Select(static e => e.Value)])
                : PyTuple.Empty;
        });

        module.Add("fields", static arguments =>
        {
            var declared = arguments[0] switch
            {
                PyClass type => type.GetAttribute("__dataclass_fields__"),
                PyInstance owner => owner.Class.GetAttribute("__dataclass_fields__"),
                _ => null,
            };

            if (declared is PyDict descriptors)
            {
                return new PyTuple([.. descriptors.Entries.Select(static entry => entry.Value)]);
            }

            var names = arguments[0] switch
            {
                PyDataclass dataclass => dataclass.FieldNames,
                PyInstance instance => [.. instance.Fields.Entries.Select(static e => e.Key.Display())],
                _ => (IReadOnlyList<string>)[],
            };

            return new PyTuple([.. names.Select(static name =>
                (PyObject)new PyDataclass("Field", ["name"], [new PyStr(name)], frozen: true))]);
        });

        return module;
    }

    /// <summary>
    /// Turns a plain class into a dataclass: gives it an <c>__init__</c>, a <c>__repr__</c>
    /// and an <c>__eq__</c> over its annotated fields.
    /// </summary>
    /// <remarks>
    /// The fields are the class's annotations, in declaration order, minus the
    /// <c>ClassVar</c>s — which is exactly how CPython decides, and why annotations are
    /// retained at run time at all. Defaults are captured here, when the decorator runs,
    /// rather than read from the class later.
    /// </remarks>
    private static PyObject Decorate(VirtualMachine machine, PyObject target, bool frozen, bool eq, bool order)
    {
        if (target is not PyClass type)
        {
            return target;
        }

        var fields = new List<Field>();
        var seenDefault = false;

        if (type.GetAttribute("__annotations__") is PyDict annotations)
        {
            foreach (var (name, annotation) in annotations.Entries)
            {
                if (IsClassVar(annotation))
                {
                    continue;
                }

                var field = name.Display();
                var hasDefault = type.Members.TryGetValue(new PyStr(field), out var value);

                if (hasDefault && value is PyList or PyDict or PySet)
                {
                    throw new PyRaise(PyErrors.ValueError(
                        $"mutable default <class '{value.TypeName}'> for field {field} is not allowed: use default_factory"));
                }

                if (seenDefault && !hasDefault)
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"non-default argument '{field}' follows default argument '{fields.Last(static f => f.HasDefault).Name}'"));
                }

                seenDefault |= hasDefault;
                fields.Add(new Field(field, hasDefault, hasDefault ? value : null));
            }
        }

        var names = fields.Select(static f => f.Name).ToList();

        var descriptors = new PyDict();

        foreach (var field in fields)
        {
            // Every field carries the decorator's defaults: `field()` and the
            // `@dataclass(...)` switches that vary them are not modelled.
            descriptors.Set(new PyStr(field.Name), new PyDataclass(
                "Field",
                ["name", "type", "default", "init", "repr", "compare", "kw_only", "hash", "doc"],
                [
                    new PyStr(field.Name),
                    type.GetAttribute("__annotations__") is PyDict declared
                        && declared.TryGetValue(new PyStr(field.Name), out var annotation)
                        ? annotation
                        : PyNone.Instance,
                    field.Default ?? PyNone.Instance,
                    PyBool.True,
                    PyBool.True,
                    PyBool.True,
                    PyBool.False,
                    PyNone.Instance,
                    PyNone.Instance,
                ],
                frozen: true));
        }

        type.SetAttribute("__dataclass_fields__", descriptors);
        type.SetAttribute("__dataclass_frozen__", PyBool.Of(frozen));

        if (type.GetAttribute("__init__") is null)
        {
            type.SetAttribute("__init__", new PyBuiltinFunction(
                $"{type.Name}.__init__",
                (arguments, keywords) => Initialize(type, fields, arguments, keywords))
            {
                BindsAsMethod = true,
            });
        }

        if (type.GetAttribute("__repr__") is null)
        {
            type.SetAttribute("__repr__", new PyBuiltinFunction(
                $"{type.Name}.__repr__",
                arguments => new PyStr(Render(machine, type, names, arguments[0]))));
        }

        if (eq && type.GetAttribute("__eq__") is null)
        {
            type.SetAttribute("__eq__", new PyBuiltinFunction(
                $"{type.Name}.__eq__",
                arguments => Equal(machine, type, names, arguments[0], arguments[1])));

            // Field-wise equality without a matching hash would break the dict invariant,
            // so an unfrozen dataclass is unhashable unless it says otherwise.
            if (!frozen && type.GetAttribute("__hash__") is null)
            {
                type.SetAttribute("__hash__", PyNone.Instance);
            }
        }

        if (frozen && type.GetAttribute("__hash__") is null)
        {
            type.SetAttribute("__hash__", new PyBuiltinFunction(
                $"{type.Name}.__hash__",
                arguments => PyInt.From(HashOf(machine, names, arguments[0]))));
        }

        _ = order;
        return type;
    }

    /// <summary>One annotated field of a dataclass.</summary>
    private sealed record Field(string Name, bool HasDefault, PyObject? Default);

    /// <summary>True for an annotation spelled <c>ClassVar</c> or <c>x.ClassVar</c>.</summary>
    private static bool IsClassVar(PyObject annotation)
    {
        var text = annotation switch
        {
            PyStr name => name.Value,
            _ => annotation.Repr(),
        };

        return text.Contains("ClassVar", StringComparison.Ordinal);
    }

    /// <summary>Binds the constructor arguments to the fields, as CPython's generated one does.</summary>
    private static PyObject Initialize(PyClass type, List<Field> fields, PyObject[] arguments, PyDict? keywords)
    {
        var instance = arguments[0];
        var positional = arguments[1..];
        var bound = new Dictionary<string, PyObject>(StringComparer.Ordinal);

        // Keywords bind first, so an unexpected one is reported before an arity error.
        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            var name = key.Display();

            if (fields.All(field => field.Name != name))
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{type.Name}.__init__() got an unexpected keyword argument '{name}'"));
            }

            var index = fields.FindIndex(field => field.Name == name);

            if (index < positional.Length)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{type.Name}.__init__() got multiple values for argument '{name}'"));
            }

            bound[name] = value;
        }

        if (positional.Length > fields.Count)
        {
            var required = fields.Count(static field => !field.HasDefault);

            throw new PyRaise(PyErrors.TypeError(required == fields.Count
                ? $"{type.Name}.__init__() takes {fields.Count + 1} positional arguments but {positional.Length + 1} were given"
                : $"{type.Name}.__init__() takes from {required + 1} to {fields.Count + 1} positional arguments "
                  + $"but {positional.Length + 1} were given"));
        }

        var missing = new List<string>();

        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];

            var value = i < positional.Length ? positional[i]
                : bound.TryGetValue(field.Name, out var keyword) ? keyword
                : field.Default;

            if (value is null)
            {
                missing.Add(field.Name);
                continue;
            }

            instance.SetAttribute(field.Name, value);
        }

        if (missing.Count > 0)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{type.Name}.__init__() missing {missing.Count} required positional "
                + $"argument{(missing.Count == 1 ? string.Empty : "s")}: {Join(missing)}"));
        }

        return PyNone.Instance;
    }

    /// <summary>Joins names the way CPython lists missing arguments: <c>'a', 'b' and 'c'</c>.</summary>
    private static string Join(IReadOnlyList<string> names)
    {
        var quoted = names.Select(static name => $"'{name}'").ToList();

        return quoted.Count == 1
            ? quoted[0]
            : string.Join(", ", quoted.Take(quoted.Count - 1)) + " and " + quoted[^1];
    }

    private static string Render(VirtualMachine machine, PyClass type, List<string> names, PyObject instance)
    {
        if (!RecursionGuard.TryEnter(instance))
        {
            return "...";
        }

        try
        {
            var parts = names.Select(name => $"{name}={Attribute(machine, type, instance, name).Repr()}");
            return $"{type.Name}({string.Join(", ", parts)})";
        }
        finally
        {
            RecursionGuard.Exit(instance);
        }
    }

    private static PyObject Equal(VirtualMachine machine, PyClass type, List<string> names, PyObject left, PyObject right)
    {
        // CPython's generated `__eq__` opens with an identity check, so a cyclic instance
        // still compares equal to itself.
        if (ReferenceEquals(left, right))
        {
            return PyBool.True;
        }

        if (right is not PyInstance instance || !ReferenceEquals(instance.Class, type))
        {
            return Builtins.NotImplementedSingleton.Instance;
        }

        // The field walk recurses in the host, so it charges the same depth budget a
        // Python-level recursion would; a cyclic pair otherwise overflows the host stack.
        using var level = machine.EnterRecursion();

        foreach (var name in names)
        {
            // Field comparison uses `==`, which consults `__eq__` even for two references
            // to the same object — unlike container membership, which shortcuts on identity.
            if (!Operators.Compare("==", Attribute(machine, type, left, name), Attribute(machine, type, right, name)).IsTruthy())
            {
                return PyBool.False;
            }
        }

        return PyBool.True;
    }

    private static System.Numerics.BigInteger HashOf(VirtualMachine machine, List<string> names, PyObject instance)
    {
        var hash = new System.Numerics.BigInteger(17);

        foreach (var name in names)
        {
            hash = (hash * 31) + Attribute(machine, null, instance, name).PyHash();
        }

        return hash;
    }

    /// <summary>Reads a field, falling back to the class attribute as attribute access does.</summary>
    private static PyObject Attribute(VirtualMachine machine, PyClass? type, PyObject instance, string name)
    {
        _ = machine;

        if (name == "__class__" && instance is PyInstance owner)
        {
            return owner.Class;
        }

        return instance.GetAttribute(name)
            ?? type?.GetAttribute(name)
            ?? throw new PyRaise(new PyException(
                PyExceptionType.AttributeError,
                $"'{instance.TypeName}' object has no attribute '{name}'"));
    }
}
