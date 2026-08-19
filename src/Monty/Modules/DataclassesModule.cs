using Monty.Runtime;

namespace Monty.Modules;

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

            if (arguments.Length == 0)
            {
                return new PyBuiltinFunction("dataclass", inner => Decorate(machine, inner[0], frozen));
            }

            return Decorate(machine, arguments[0], frozen);
        }));

        module.Add("field", new PyBuiltinFunction("field", static (_, keywords) =>
            keywords is not null && keywords.TryGetValue(new PyStr("default"), out var value)
                ? value
                : PyNone.Instance));

        module.Add("is_dataclass", static arguments =>
            PyBool.Of(arguments[0] is PyDataclass
                || (arguments[0] is PyClass type && type.GetAttribute("__dataclass_fields__") is not null)));

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
            var names = arguments[0] switch
            {
                PyDataclass dataclass => dataclass.FieldNames,
                PyInstance instance => [.. instance.Fields.Entries.Select(static e => e.Key.Display())],
                PyClass type when type.GetAttribute("__dataclass_fields__") is PyList declared =>
                    [.. declared.Items.Select(static f => f.Display())],
                _ => (IReadOnlyList<string>)[],
            };

            return new PyTuple([.. names.Select(static name =>
                (PyObject)new PyDataclass("Field", ["name"], [new PyStr(name)], frozen: true))]);
        });

        return module;
    }

    /// <summary>
    /// Turns a plain class into a dataclass: gives it an <c>__init__</c> that binds the
    /// annotated fields, and records the field list for reflection.
    /// </summary>
    private static PyObject Decorate(VirtualMachine machine, PyObject target, bool frozen)
    {
        if (target is not PyClass type)
        {
            return target;
        }

        // Annotations are not retained at run time, so the fields are the class-level
        // names that carry a value, in declaration order.
        var fields = type.Members.Entries
            .Where(static entry => entry.Value is not PyFunction)
            .Select(static entry => entry.Key.Display())
            .Where(static name => !name.StartsWith("__", StringComparison.Ordinal))
            .ToList();

        type.SetAttribute("__dataclass_fields__", new PyList([.. fields.Select(static f => (PyObject)new PyStr(f))]));
        type.SetAttribute("__dataclass_frozen__", PyBool.Of(frozen));

        if (type.GetAttribute("__init__") is null)
        {
            type.SetAttribute("__init__", new PyBuiltinFunction("__init__", (arguments, keywords) =>
            {
                var instance = arguments[0];

                for (var i = 0; i < fields.Count; i++)
                {
                    var supplied = i + 1 < arguments.Length
                        ? arguments[i + 1]
                        : keywords is not null && keywords.TryGetValue(new PyStr(fields[i]), out var keyword)
                            ? keyword
                            : type.GetAttribute(fields[i]) ?? PyNone.Instance;

                    instance.SetAttribute(fields[i], supplied);
                }

                return PyNone.Instance;
            }));
        }

        _ = machine;
        return type;
    }
}
