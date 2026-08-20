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

        module.Add("Counter", PyCounter.Type);

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

        module.Add("defaultdict", PyDefaultDict.MakeType(machine));

        module.Add("deque", PyDeque.Type);

        module.Add("namedtuple", new PyBuiltinFunction("namedtuple", static (arguments, keywords) =>
        {
            var fields = PyNamedTupleType.ParseFields(arguments[1]);
            var rename = Keyword(arguments, keywords, 2, "rename")?.IsTruthy() ?? false;

            // CPython coerces the type name with `str()` before validating it, so anything
            // with a `__str__` that yields an identifier is accepted.
            var name = arguments[0].Display();
            PyNamedTupleType.Validate(name, fields, rename);

            var defaults = Keyword(arguments, keywords, 3, "defaults") is { } supplied and not PyNone
                ? VirtualMachine.RequireIterable(supplied).ToList()
                : [];

            var module = Keyword(arguments, keywords, 4, "module") is { } named and not PyNone
                ? named
                : new PyStr("__main__");

            return new PyNamedTupleType(name, fields, defaults, module);
        }));

        return module;
    }

    /// <summary>Reads an argument that may be given positionally or by name.</summary>
    private static PyObject? Keyword(PyObject[] arguments, PyDict? keywords, int position, string name) =>
        arguments.Length > position ? arguments[position]
        : keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) ? value
        : null;

    /// <summary>Builds <c>itertools</c>.</summary>
    public static PyModuleObject CreateItertools(VirtualMachine machine)
    {
        var module = new PyModuleObject("itertools");

        // The adaptors are lazy: each holds its source and pulls one item at a time, so an
        // infinite source such as `count()` composes with `islice` the way it does upstream.
        module.Add("chain", new PyBuiltinFunction("chain", static (arguments, keywords) =>
        {
            PyBuiltinFunction.RejectKeywords("chain", keywords);

            var index = 0;
            Func<PyObject?>? current = null;
            var spent = false;

            return Adaptor("chain", () =>
            {
                while (!spent)
                {
                    if (current is null)
                    {
                        if (index == arguments.Length)
                        {
                            spent = true;
                            return null;
                        }

                        // A source that will not iterate ends the chain: CPython drops the
                        // source, so the arguments after it are never reached. One whose
                        // `__next__` raises stays in place and raises again.
                        try
                        {
                            current = VirtualMachine.Puller(arguments[index++]);
                        }
                        catch (PyRaise)
                        {
                            spent = true;
                            throw;
                        }
                    }

                    if (current() is { } item)
                    {
                        return item;
                    }

                    current = null;
                }

                return null;
            });
        }));

        module.Add("islice", new PyBuiltinFunction("islice", static (arguments, keywords) =>
        {
            PyBuiltinFunction.RejectKeywords("islice", keywords);
            Arity.Between("islice", arguments, 2, 4);

            // `islice(it, stop)` and `islice(it, start[, stop[, step]])` are both valid,
            // and the two-argument form names the stop in its error.
            var single = arguments.Length == 2;
            var start = single ? 0 : Index(arguments[1], single);
            var stop = Index(single ? arguments[1] : arguments.Length > 2 ? arguments[2] : PyNone.Instance, single);
            var step = arguments.Length > 3 ? Step(arguments[3]) : 1;

            var source = VirtualMachine.RequireIterable(arguments[0]).GetEnumerator();
            var position = 0;

            return Adaptor("islice", () =>
            {
                while (position < start && source.MoveNext())
                {
                    position++;
                }

                if (position < start || (stop is { } limit && position >= limit))
                {
                    return null;
                }

                if (!source.MoveNext())
                {
                    return null;
                }

                var item = source.Current;
                position++;

                // The step is consumed eagerly so the next call starts on an item.
                for (var i = 1; i < step && (stop is null || position < stop) && source.MoveNext(); i++)
                {
                    position++;
                }

                return item;
            });
        }));

        module.Add("count", new PyBuiltinFunction("count", static (arguments, keywords) =>
        {
            Accept(arguments, keywords, "count", "start", "step");

            // The start and step may be floats, so they stay Python values rather than
            // being narrowed to integers.
            var start = Argument(arguments, keywords, 0, "start", "count") is { } from
                ? Number(from)
                : new PyInt(0);

            var step = Argument(arguments, keywords, 1, "step", "count") is { } by
                ? Number(by)
                : new PyInt(1);

            var next = start;

            return new PyIterator(
                () =>
                {
                    var value = next;
                    next = Operators.Binary("+", next, step);
                    return value;
                },
                "itertools.count")
            {
                // The step is shown unless it is an integer equal to 1, and the position
                // shown is the current one, not the start.
                Describe = () => step is PyInt { Value.IsOne: true }
                    ? $"count({next.Repr()})"
                    : $"count({next.Repr()}, {step.Repr()})",
            };
        }));

        module.Add("repeat", new PyBuiltinFunction("repeat", static (arguments, keywords) =>
        {
            // The total arity is checked first, then the required argument, and only then
            // an unexpected keyword — which is the order CPython reports them in.
            if (arguments.Length + (keywords?.Count ?? 0) > 2)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"repeat() takes at most 2 arguments ({arguments.Length + (keywords?.Count ?? 0)} given)"));
            }

            var value = Argument(arguments, keywords, 0, "object", "repeat")
                ?? throw new PyRaise(PyErrors.TypeError(
                    "repeat() missing required argument 'object' (pos 1)"));

            Accept(arguments, keywords, "repeat", "object", "times");

            var times = Argument(arguments, keywords, 1, "times", "repeat") is { } count
                ? Ssize(count)
                : (BigInteger?)null;

            var produced = 0;

            return new PyIterator(
                () => times is { } limit && produced >= limit ? null : Produce(ref produced, value),
                "itertools.repeat")
            {
                // The count shown is what remains, and an endless repeat shows none.
                Describe = () => times is { } limit
                    ? $"repeat({value.Repr()}, {BigInteger.Max(0, limit - produced)})"
                    : $"repeat({value.Repr()})",
            };
        }));

        module.Add("cycle", new PyBuiltinFunction("cycle", static (arguments, keywords) =>
        {
            PyBuiltinFunction.RejectKeywords("cycle", keywords);
            Arity.ExactCount("cycle", arguments, 1);

            // Unlike chain, cycle resolves its source at once.
            var source = VirtualMachine.RequireIterable(arguments[0]).GetEnumerator();
            var saved = new List<PyObject>();
            var index = 0;
            var draining = true;

            return Adaptor("cycle", () =>
            {
                if (draining)
                {
                    if (source.MoveNext())
                    {
                        saved.Add(source.Current);
                        return source.Current;
                    }

                    draining = false;
                }

                if (saved.Count == 0)
                {
                    return null;
                }

                var item = saved[index];
                index = (index + 1) % saved.Count;
                return item;
            });
        }));

        module.Add("compress", new PyBuiltinFunction("compress", static (arguments, keywords) =>
        {
            Accept(arguments, keywords, "compress", "data", "selectors");

            var data = Argument(arguments, keywords, 0, "data", "compress")
                ?? throw new PyRaise(PyErrors.TypeError(
                    "compress() missing required argument 'data' (pos 1)"));

            var selectors = Argument(arguments, keywords, 1, "selectors", "compress")
                ?? throw new PyRaise(PyErrors.TypeError(
                    "compress() missing required argument 'selectors' (pos 2)"));

            var items = VirtualMachine.RequireIterable(data).GetEnumerator();
            var flags = VirtualMachine.RequireIterable(selectors).GetEnumerator();

            return Adaptor("compress", () =>
            {
                // Both sides advance together, so the shorter one ends it.
                while (items.MoveNext() && flags.MoveNext())
                {
                    if (flags.Current.IsTruthy())
                    {
                        return items.Current;
                    }
                }

                return null;
            });
        }));

        module.Add("pairwise", new PyBuiltinFunction("pairwise", static (arguments, keywords) =>
        {
            PyBuiltinFunction.RejectKeywords("pairwise", keywords);
            Arity.ExactCount("pairwise", arguments, 1);

            var source = VirtualMachine.RequireIterable(arguments[0]).GetEnumerator();
            PyObject? previous = null;

            return Adaptor("pairwise", () =>
            {
                if (previous is null)
                {
                    if (!source.MoveNext())
                    {
                        return null;
                    }

                    previous = source.Current;
                }

                if (!source.MoveNext())
                {
                    return null;
                }

                var pair = new PyTuple([previous, source.Current]);
                previous = source.Current;
                return pair;
            });
        }));

        module.Add("product", static (arguments, keywords) =>
        {
            var repeat = keywords is not null && keywords.TryGetValue(new PyStr("repeat"), out var value)
                ? (int)RequireNumber(value)
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
            var length = arguments.Length > 1 ? (int)RequireNumber(arguments[1]) : items.Count;
            return new PyIterator(Permute(items, length).Select(static row => (PyObject)new PyTuple(row)).ToList());
        });

        module.Add("combinations", static arguments =>
        {
            var items = VirtualMachine.RequireIterable(arguments[0]).ToList();
            var length = (int)RequireNumber(arguments[1]);
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

        module.Add("takewhile", arguments =>
        {
            var results = new List<PyObject>();

            foreach (var item in VirtualMachine.RequireIterable(arguments[1]))
            {
                if (!machine.Call(arguments[0], [item]).IsTruthy())
                {
                    break;
                }

                results.Add(item);
            }

            return new PyIterator(results);
        });

        module.Add("dropwhile", arguments =>
        {
            var results = new List<PyObject>();
            var dropping = true;

            foreach (var item in VirtualMachine.RequireIterable(arguments[1]))
            {
                if (dropping && machine.Call(arguments[0], [item]).IsTruthy())
                {
                    continue;
                }

                dropping = false;
                results.Add(item);
            }

            return new PyIterator(results);
        });

        module.Add("filterfalse", arguments => new PyIterator(
            [.. VirtualMachine.RequireIterable(arguments[1])
                .Where(item => !machine.Call(arguments[0], [item]).IsTruthy())]));

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
    /// Builds the <c>gc</c> module.
    /// </summary>
    /// <remarks>
    /// The port has no collector of its own — objects belong to the host runtime — so these
    /// are the honest no-ops that shape implies. Reaching the host's collector from
    /// sandboxed code would let a program stall its host at will, and reporting real counts
    /// would leak the host's heap state, so neither is done.
    /// </remarks>
    /// <returns>The module.</returns>
    public static PyModuleObject CreateGc()
    {
        var module = new PyModuleObject("gc");

        module.Add("collect", new PyBuiltinFunction("collect", static _ => new PyInt(0)));
        module.Add("disable", new PyBuiltinFunction("disable", static _ => PyNone.Instance));
        module.Add("enable", new PyBuiltinFunction("enable", static _ => PyNone.Instance));
        module.Add("isenabled", new PyBuiltinFunction("isenabled", static _ => PyBool.True));

        return module;
    }

    /// <summary>
    /// Builds <c>typing</c>. Annotations are not evaluated at run time, so the names only
    /// need to exist.
    /// </summary>
    /// <returns>The module.</returns>
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

        // `Union` is a class in CPython, not a special form, and its repr shows it.
        module.Add("Union", new PyType(
            "typing.Union",
            static _ => false,
            static (_, _) => throw new PyRaise(PyErrors.TypeError("cannot instantiate typing.Union"))));

        // Guarded imports are for type checkers only, so this is False at runtime and the
        // block it guards never runs.
        module.Add("TYPE_CHECKING", PyBool.False);

        return module;
    }

    /// <summary>Wraps a pull function as one of itertools' iterator types.</summary>
    private static PyIterator Adaptor(string name, Func<PyObject?> advance) =>
        new(advance, "itertools." + name);

    /// <summary>Yields the repeated value, counting the ones produced.</summary>
    private static PyObject Produce(ref int produced, PyObject value)
    {
        produced++;
        return value;
    }

    /// <summary>
    /// Enforces an arity counted over positionals and keywords together, and rejects a
    /// keyword that names no parameter.
    /// </summary>
    private static void Accept(PyObject[] arguments, PyDict? keywords, string function, params string[] names)
    {
        var total = arguments.Length + (keywords?.Count ?? 0);

        if (total > names.Length)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{function}() takes at most {names.Length} arguments ({total} given)"));
        }

        foreach (var (key, _) in keywords?.Entries ?? [])
        {
            if (!names.Contains(key.Display()))
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{function}() got an unexpected keyword argument '{key.Display()}'"));
            }
        }
    }

    /// <summary>Reads an argument given positionally or by name, rejecting a duplicate.</summary>
    private static PyObject? Argument(
        PyObject[] arguments, PyDict? keywords, int position, string name, string function)
    {
        var named = keywords?.TryGetValue(new PyStr(name), out var value) == true ? value : null;

        if (arguments.Length > position)
        {
            return named is null
                ? arguments[position]
                : throw new PyRaise(PyErrors.TypeError(
                    $"{function}() got multiple values for argument '{name}'"));
        }

        return named;
    }

    /// <summary>Reads a count that must fit the host's index type, as CPython's `n` unit does.</summary>
    private static BigInteger Ssize(PyObject value)
    {
        if (value is not PyInt count)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"'{value.TypeName}' object cannot be interpreted as an integer"));
        }

        return BigInteger.Abs(count.Value) > long.MaxValue
            ? throw new PyRaise(new PyException(
                PyExceptionType.OverflowError, "Python int too large to convert to C ssize_t"))
            : count.Value;
    }

    /// <summary>Reads one of islice's bounds, which must be a non-negative integer or None.</summary>
    private static int? Index(PyObject value, bool single)
    {
        if (value is PyNone)
        {
            return null;
        }

        // The two-argument form has only one bound to blame, so it names it.
        var message = single
            ? "Stop argument for islice() must be None or an integer: 0 <= x <= sys.maxsize."
            : "Indices for islice() must be None or an integer: 0 <= x <= sys.maxsize.";

        return value is PyInt index && index.Value >= 0 && index.Value <= int.MaxValue
            ? (int)index.Value
            : throw new PyRaise(PyErrors.ValueError(message));
    }

    private static int Step(PyObject value) =>
        value is PyNone ? 1
        : value is PyInt step && step.Value > 0 && step.Value <= int.MaxValue
            ? (int)step.Value
            : throw new PyRaise(PyErrors.ValueError(
                "Step for islice() must be a positive integer or None."));

    private static int Bound(PyObject value, int length) =>
        value is PyNone ? length : (int)RequireNumber(value);

    private static BigInteger RequireNumber(PyObject value) => value switch
    {
        PyInt integer => integer.Value,
        _ => throw new PyRaise(PyErrors.TypeError(
            $"'{value.TypeName}' object cannot be interpreted as an integer")),
    };

    /// <summary>
    /// Accepts any number, which is what <c>count</c> takes — its start and step may be
    /// floats. A bool widens to the int it is.
    /// </summary>
    private static PyObject Number(PyObject value) => value switch
    {
        PyBool flag => new PyInt(flag.Value ? 1 : 0),
        PyInt or PyFloat => value,
        _ => throw new PyRaise(PyErrors.TypeError("a number is required")),
    };

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
    /// <remarks>
    /// CPython gives each construct its own internal type; Monty uses one, so
    /// <c>type(x)</c> reports <c>typing._SpecialForm</c> for all of them.
    /// </remarks>
    public override string TypeName => "typing._SpecialForm";

    /// <inheritdoc />
    public override string Repr() => "typing." + name;

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index) => this;

    /// <inheritdoc />
    public override PyObject? GetAttribute(string attribute) => null;
}

/// <summary>
/// <c>collections.defaultdict</c>.
/// </summary>
/// <remarks>
/// A real dict subclass, because scripts check: `isinstance(d, dict)` holds, and every dict
/// method works on it unchanged. All it adds is what happens on a missing key.
/// </remarks>
public sealed class PyDefaultDict : PyDict
{
    private readonly VirtualMachine _machine;

    /// <summary>Creates a defaultdict.</summary>
    /// <param name="machine">The interpreter that calls the factory.</param>
    /// <param name="type">The type object this instance reports.</param>
    /// <param name="factory">What builds a missing value, or null for none.</param>
    public PyDefaultDict(VirtualMachine machine, PyType type, PyObject? factory)
    {
        _machine = machine;
        Type = type;
        Factory = factory;
    }

    /// <summary>
    /// Builds the type object for one interpreter.
    /// </summary>
    /// <remarks>
    /// One per interpreter rather than one shared statically, because constructing a
    /// defaultdict needs the interpreter that will later call its factory — and two
    /// sandboxes must share no mutable state.
    /// </remarks>
    /// <param name="machine">The interpreter this type belongs to.</param>
    /// <returns>The type object, which is also what <c>type(d)</c> answers.</returns>
    public static PyType MakeType(VirtualMachine machine)
    {
        PyType? self = null;

        self = new PyType(
            "collections.defaultdict",
            static value => value is PyDefaultDict,
            (arguments, keywords) => Create(machine, self!, arguments, keywords));

        // Unlike Counter, defaultdict keeps the inherited classmethod — and it builds a
        // defaultdict, with no factory, rather than a plain dict.
        self.Members["fromkeys"] = new PyBuiltinFunction("fromkeys", arguments =>
        {
            // Inherited from dict, so the arity complaints name the bare method.
            if (arguments.Length == 0)
            {
                throw new PyRaise(PyErrors.TypeError("fromkeys expected at least 1 argument, got 0"));
            }

            if (arguments.Length > 2)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"fromkeys expected at most 2 arguments, got {arguments.Length}"));
            }

            var created = new PyDefaultDict(machine, self, null);
            var value = arguments.Length > 1 ? arguments[1] : PyNone.Instance;

            foreach (var key in VirtualMachine.RequireIterable(arguments[0]))
            {
                created.Set(key, value);
            }

            return created;
        });

        return self;
    }

    /// <summary>The type object this instance reports, which is the module's own.</summary>
    public PyType Type { get; }

    /// <summary>What builds a missing value, or null when there is none.</summary>
    /// <remarks>
    /// Writable, and CPython does not check what it is written: a non-callable is stored as
    /// it stands and only fails when a missing key actually asks it for a value.
    /// </remarks>
    public PyObject? Factory { get; set; }

    /// <inheritdoc />
    public override string TypeName => "collections.defaultdict";

    /// <summary>Builds one from the constructor's arguments.</summary>
    /// <param name="machine">The interpreter that calls the factory.</param>
    /// <param name="type">The type object the new instance reports.</param>
    /// <param name="arguments">The factory, then any initial data.</param>
    /// <param name="keywords">Initial entries given by name.</param>
    /// <returns>The new defaultdict.</returns>
    public static PyObject Create(VirtualMachine machine, PyType type, PyObject[] arguments, PyDict? keywords)
    {
        var factory = arguments.Length > 0 && arguments[0] is not PyNone ? arguments[0] : null;

        // The constructor is stricter than the setter: it rejects a non-callable outright.
        if (factory is not (null or PyCallable or PyType or PyClass or PyNamedTupleType or PyExceptionType))
        {
            throw new PyRaise(PyErrors.TypeError("first argument must be callable or None"));
        }

        var created = new PyDefaultDict(machine, type, factory);
        var initial = TypeRegistry.Dict.Construct([.. arguments.Skip(1)], keywords);

        foreach (var (key, value) in ((PyDict)initial).Entries)
        {
            created.Set(key, value);
        }

        return created;
    }

    /// <inheritdoc />
    public override string Repr() => $"defaultdict({Factory?.Repr() ?? "None"}, {base.Repr()})";

    /// <inheritdoc />
    public override PyObject GetItem(PyObject index) =>
        TryGetValue(index, out var value) ? value : Missing(index);

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "default_factory" => Factory ?? PyNone.Instance,
        "__missing__" => new PyBuiltinFunction("__missing__", arguments => Missing(arguments[0])),
        _ => base.GetAttribute(name),
    };

    /// <inheritdoc />
    public override bool SetAttribute(string name, PyObject value)
    {
        if (name != "default_factory")
        {
            return base.SetAttribute(name, value);
        }

        Factory = value is PyNone ? null : value;
        return true;
    }

    /// <inheritdoc />
    protected override PyDict Blank() => new PyDefaultDict(_machine, Type, Factory);

    /// <inheritdoc />
    public override PyDict Fresh() => new PyDefaultDict(_machine, Type, null);

    /// <summary>Builds the value a missing key gets, and stores it.</summary>
    private PyObject Missing(PyObject key)
    {
        // A missing key is created from the factory, which is the whole point of the type.
        if (Factory is null)
        {
            throw new PyRaise(PyErrors.KeyError(key));
        }

        var created = _machine.Call(Factory, []);
        Set(key, created);
        return created;
    }
}
