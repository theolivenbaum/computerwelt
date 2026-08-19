using System.Numerics;
using Monty.Runtime;

namespace Monty.Modules;

/// <summary>The <c>collections</c>, <c>itertools</c> and <c>typing</c> modules.</summary>
public static class SupportModules
{
    /// <summary>Builds <c>collections</c>.</summary>
    public static PyModuleObject CreateCollections(VirtualMachine machine)
    {
        var module = new PyModuleObject("collections");

        // A Counter is a dict of counts; building it as a plain dict keeps every dict
        // method working on it, which is what the fixtures exercise.
        module.Add("Counter", new PyBuiltinFunction("Counter", arguments =>
        {
            var counter = new PyDict();

            if (arguments.Length == 0)
            {
                return counter;
            }

            if (arguments[0] is PyDict source)
            {
                foreach (var (key, value) in source.Entries)
                {
                    counter.Set(key, value);
                }

                return counter;
            }

            foreach (var item in VirtualMachine.RequireIterable(arguments[0]))
            {
                var current = counter.TryGetValue(item, out var existing) ? ((PyInt)existing).Value : BigInteger.Zero;
                counter.Set(item, new PyInt(current + 1));
            }

            return counter;
        }));

        module.Add("OrderedDict", new PyBuiltinFunction("OrderedDict", static arguments =>
        {
            var dict = new PyDict();

            if (arguments.Length > 0 && arguments[0] is PyDict source)
            {
                foreach (var (key, value) in source.Entries)
                {
                    dict.Set(key, value);
                }
            }

            return dict;
        }));

        module.Add("defaultdict", new PyBuiltinFunction("defaultdict", arguments =>
            new PyDefaultDict(machine, arguments.Length > 0 && arguments[0] is not PyNone ? arguments[0] : null)));

        module.Add("deque", new PyBuiltinFunction("deque", static arguments =>
            new PyList(arguments.Length == 0 ? [] : VirtualMachine.RequireIterable(arguments[0]).ToList())));

        module.Add("namedtuple", new PyBuiltinFunction("namedtuple", static arguments =>
        {
            var fields = arguments[1] is PyStr text
                ? text.Value.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : [.. VirtualMachine.RequireIterable(arguments[1]).Select(static f => f.Display())];

            return new NamedTupleFactory(arguments[0].Display(), fields);
        }));

        return module;
    }

    /// <summary>Builds <c>itertools</c>.</summary>
    public static PyModuleObject CreateItertools()
    {
        var module = new PyModuleObject("itertools");

        module.Add("chain", static arguments =>
            new PyIterator(arguments.SelectMany(VirtualMachine.RequireIterable).ToList()));

        module.Add("islice", static arguments =>
        {
            var items = VirtualMachine.RequireIterable(arguments[0]).ToList();

            // `islice(it, stop)` and `islice(it, start, stop[, step])` are both valid.
            var (start, stop, step) = arguments.Length switch
            {
                2 => (0, Bound(arguments[1], items.Count), 1),
                3 => (Bound(arguments[1], items.Count), Bound(arguments[2], items.Count), 1),
                _ => (Bound(arguments[1], items.Count), Bound(arguments[2], items.Count), (int)((PyInt)arguments[3]).Value),
            };

            var result = new List<PyObject>();

            for (var i = start; i < Math.Min(stop, items.Count); i += Math.Max(1, step))
            {
                result.Add(items[i]);
            }

            return new PyIterator(result);
        });

        module.Add("count", static arguments =>
        {
            var start = arguments.Length > 0 ? ((PyInt)arguments[0]).Value : BigInteger.Zero;
            var step = arguments.Length > 1 ? ((PyInt)arguments[1]).Value : BigInteger.One;

            // Unbounded in Python; bounded here, because an infinite iterator materialized
            // into a list would never terminate. The cap is generous and documented.
            var values = new List<PyObject>(10_000);

            for (var i = 0; i < 10_000; i++)
            {
                values.Add(new PyInt(start + (i * step)));
            }

            return new PyIterator(values);
        });

        module.Add("repeat", static arguments =>
        {
            var times = arguments.Length > 1 ? (int)((PyInt)arguments[1]).Value : 10_000;
            return new PyIterator(Enumerable.Repeat(arguments[0], Math.Max(0, times)).ToList());
        });

        module.Add("product", static (arguments, keywords) =>
        {
            var repeat = keywords is not null && keywords.TryGetValue(new PyStr("repeat"), out var value)
                ? (int)((PyInt)value).Value
                : 1;

            var pools = new List<List<PyObject>>();

            for (var i = 0; i < repeat; i++)
            {
                pools.AddRange(arguments.Select(static a => VirtualMachine.RequireIterable(a).ToList()));
            }

            var rows = new List<List<PyObject>> { new List<PyObject>() };

            foreach (var pool in pools)
            {
                rows = rows.SelectMany(row => pool.Select(item => new List<PyObject>(row) { item })).ToList();
            }

            return new PyIterator(rows.Select(static row => (PyObject)new PyTuple(row)).ToList());
        });

        module.Add("permutations", static arguments =>
        {
            var items = VirtualMachine.RequireIterable(arguments[0]).ToList();
            var length = arguments.Length > 1 ? (int)((PyInt)arguments[1]).Value : items.Count;
            return new PyIterator(Permute(items, length).Select(static row => (PyObject)new PyTuple(row)).ToList());
        });

        module.Add("combinations", static arguments =>
        {
            var items = VirtualMachine.RequireIterable(arguments[0]).ToList();
            var length = (int)((PyInt)arguments[1]).Value;
            return new PyIterator(Combine(items, length, 0).Select(static row => (PyObject)new PyTuple(row)).ToList());
        });

        module.Add("accumulate", static arguments =>
        {
            var items = VirtualMachine.RequireIterable(arguments[0]).ToList();
            var totals = new List<PyObject>(items.Count);
            PyObject? running = null;

            foreach (var item in items)
            {
                running = running is null ? item : Operators.Binary("+", running, item);
                totals.Add(running);
            }

            return new PyIterator(totals);
        });

        module.Add("zip_longest", static (arguments, keywords) =>
        {
            var fill = keywords is not null && keywords.TryGetValue(new PyStr("fillvalue"), out var value)
                ? value
                : PyNone.Instance;

            var sequences = arguments.Select(static a => VirtualMachine.RequireIterable(a).ToList()).ToList();
            var length = sequences.Count == 0 ? 0 : sequences.Max(static s => s.Count);
            var rows = new List<PyObject>(length);

            for (var i = 0; i < length; i++)
            {
                rows.Add(new PyTuple(sequences.Select(s => i < s.Count ? s[i] : fill).ToList()));
            }

            return new PyIterator(rows);
        });

        return module;
    }

    /// <summary>
    /// Builds <c>typing</c>. Annotations are not evaluated at run time, so the names only
    /// need to exist.
    /// </summary>
    public static PyModuleObject CreateTyping()
    {
        var module = new PyModuleObject("typing");

        foreach (var name in new[]
        {
            "Any", "List", "Dict", "Set", "Tuple", "Optional", "Union", "Callable",
            "Iterable", "Iterator", "Sequence", "Mapping", "TypeVar", "Generic",
            "Final", "Literal", "ClassVar", "NamedTuple", "TypedDict", "Protocol",
            "Self", "Never", "NoReturn", "Annotated",
        })
        {
            module.Add(name, new TypeAlias(name));
        }

        return module;
    }

    private static int Bound(PyObject value, int length) =>
        value is PyNone ? length : (int)((PyInt)value).Value;

    private static IEnumerable<List<PyObject>> Permute(List<PyObject> items, int length)
    {
        if (length == 0)
        {
            yield return [];
            yield break;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var rest = new List<PyObject>(items);
            rest.RemoveAt(i);

            foreach (var tail in Permute(rest, length - 1))
            {
                yield return [items[i], .. tail];
            }
        }
    }

    private static IEnumerable<List<PyObject>> Combine(List<PyObject> items, int length, int start)
    {
        if (length == 0)
        {
            yield return [];
            yield break;
        }

        for (var i = start; i <= items.Count - length; i++)
        {
            foreach (var tail in Combine(items, length - 1, i + 1))
            {
                yield return [items[i], .. tail];
            }
        }
    }
}

/// <summary>A placeholder for a name from <c>typing</c>, which exists only to be annotated with.</summary>
internal sealed class TypeAlias(string name) : PyObject
{
    /// <inheritdoc />
    public override string TypeName => "typing." + name;

    /// <inheritdoc />
    public override string Repr() => "typing." + name;

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index) => this;

    /// <inheritdoc />
    public override PyObject? GetAttribute(string attribute) => null;
}

/// <summary><c>collections.defaultdict</c>.</summary>
public sealed class PyDefaultDict(VirtualMachine machine, PyObject? factory) : PyObject
{
    private readonly PyDict _entries = new();

    /// <inheritdoc />
    public override string TypeName => "defaultdict";

    /// <inheritdoc />
    public override string Repr() => $"defaultdict({factory?.Repr() ?? "None"}, {_entries.Repr()})";

    /// <inheritdoc />
    public override bool IsTruthy() => _entries.Count > 0;

    /// <inheritdoc />
    public override int? Length() => _entries.Count;

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate() => _entries.Iterate();

    /// <inheritdoc />
    public override bool Contains(PyObject item) => _entries.Contains(item);

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) =>
        other is PyDefaultDict defaults ? _entries.PyEquals(defaults._entries) : _entries.PyEquals(other);

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index)
    {
        if (_entries.TryGetValue(index, out var value))
        {
            return value;
        }

        // A missing key is created from the factory, which is the whole point of the type.
        if (factory is null)
        {
            throw new PyRaise(PyErrors.KeyError(index));
        }

        var created = machine.Call(factory, []);
        _entries.Set(index, created);
        return created;
    }

    /// <inheritdoc />
    public override void SetItem(PyObject index, PyObject value) => _entries.Set(index, value);

    /// <inheritdoc />
    public override void DeleteItem(PyObject index) => _entries.DeleteItem(index);

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) =>
        // Delegating to the wrapped dict gives every dict method for free.
        name == "default_factory" ? factory ?? PyNone.Instance : _entries.GetAttribute(name);

    /// <summary>The wrapped dictionary, for method dispatch.</summary>
    public PyDict Entries => _entries;
}

/// <summary>The class object <c>namedtuple</c> returns.</summary>
public sealed class NamedTupleFactory(string name, string[] fields) : PyCallable
{
    /// <inheritdoc />
    public override string Name => name;

    /// <inheritdoc />
    public override string TypeName => "type";

    /// <inheritdoc />
    public override string Repr() => $"<class '{name}'>";

    /// <summary>The field names, in order.</summary>
    public string[] Fields => fields;

    /// <summary>Builds an instance from positional and keyword arguments.</summary>
    public PyObject Instantiate(PyObject[] arguments, PyDict? keywords)
    {
        var values = new List<PyObject>(fields.Length);

        for (var i = 0; i < fields.Length; i++)
        {
            if (i < arguments.Length)
            {
                values.Add(arguments[i]);
                continue;
            }

            if (keywords is not null && keywords.TryGetValue(new PyStr(fields[i]), out var value))
            {
                values.Add(value);
                continue;
            }

            throw new PyRaise(PyErrors.TypeError($"{name}() missing argument: '{fields[i]}'"));
        }

        return new NamedTupleInstance(this, values);
    }
}

/// <summary>An instance of a named tuple: a tuple whose fields also have names.</summary>
internal sealed class NamedTupleInstance(NamedTupleFactory factory, IReadOnlyList<PyObject> values) : PyObject
{
    /// <summary>The field values, in declaration order.</summary>
    public IReadOnlyList<PyObject> Values => values;

    /// <inheritdoc />
    public override string TypeName => factory.Name;

    /// <inheritdoc />
    public override string Repr() =>
        factory.Name + "(" + string.Join(", ", factory.Fields.Zip(values, static (field, value) => $"{field}={value.Repr()}")) + ")";

    /// <inheritdoc />
    public override int? Length() => values.Count;

    /// <inheritdoc />
    public override IEnumerable<PyObject>? Iterate() => values;

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index) => new PyTuple(values).GetItem(index);

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) => new PyTuple(values).PyEquals(
        other is NamedTupleInstance named ? new PyTuple(named.Values) : other);

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name)
    {
        var index = Array.IndexOf(factory.Fields, name);
        return index >= 0 ? values[index] : null;
    }
}
