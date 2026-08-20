namespace Monty.Runtime;

/// <summary>
/// A built-in type, as a first-class object.
/// </summary>
/// <remarks>
/// <c>type(x) is int</c> and <c>isinstance(x, int)</c> both require the name <c>int</c> to
/// denote one identity that is also callable as a constructor. Modelling built-in types as
/// plain functions — the obvious shortcut — makes both of those false.
/// </remarks>
public sealed class PyType : PyCallable
{
    private readonly Func<PyObject[], PyDict?, PyObject> _construct;
    private readonly Func<PyObject, bool> _matches;

    /// <summary>Creates a type with a constructor and an instance test.</summary>
    public PyType(string name, Func<PyObject, bool> matches, Func<PyObject[], PyDict?, PyObject> construct)
    {
        Name = name;
        _matches = matches;
        _construct = construct;
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string TypeName => "type";

    /// <inheritdoc />
    public override string Repr() => $"<class '{Name}'>";

    /// <inheritdoc />
    /// <remarks>
    /// <c>list[int]</c> is a type annotation, not an operation: subscripting a type yields
    /// the type itself, which is all a runtime that erases annotations needs it to mean.
    /// </remarks>
    public override PyObject GetItem(PyObject index)
    {
        _ = index;
        return this;
    }

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override System.Numerics.BigInteger PyHash() =>
        new(StringComparer.Ordinal.GetHashCode(Name));

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name)
    {
        if (name is "__name__" or "__qualname__")
        {
            return new PyStr(Name);
        }

        // CPython disables the inherited classmethod on Counter, and it is reachable from
        // the type as well as from an instance.
        if (Name == "Counter" && name == "fromkeys")
        {
            return new PyBuiltinFunction("fromkeys", static _ =>
                throw new PyRaise(new PyException(
                    PyExceptionType.NotImplementedError,
                    "Counter.fromkeys() is undefined.  Use Counter(iterable) instead.")));
        }

        // `bytes.fromhex(...)` and `dict.fromkeys(...)` are reached through the type, not
        // an instance.
        if (Name == "bytes" && name == "fromhex")
        {
            return new PyBuiltinFunction("fromhex", static arguments => TypeRegistry.FromHex(arguments[0].Display()));
        }

        // `dict.fromkeys(...)` is reached through the type, not an instance.
        if (Name == "dict" && name == "fromkeys")
        {
            return new PyBuiltinFunction("fromkeys", static arguments =>
            {
                var dict = new PyDict();
                var value = arguments.Length > 1 ? arguments[1] : PyNone.Instance;

                foreach (var key in VirtualMachine.RequireIterable(arguments[0]))
                {
                    dict.Set(key, value);
                }

                return dict;
            });
        }

        return null;
    }

    /// <summary>Constructs an instance.</summary>
    public PyObject Construct(PyObject[] arguments, PyDict? keywords) => _construct(arguments, keywords);

    /// <summary>True when <paramref name="value"/> is an instance of this type.</summary>
    public bool Matches(PyObject value) => _matches(value);
}

/// <summary>The built-in type objects, and the mapping from a value to its type.</summary>
public static class TypeRegistry
{
    /// <summary>Every built-in type, by name.</summary>
    public static Dictionary<string, PyType> All { get; } = new(StringComparer.Ordinal);

    private static PyType Define(string name, Func<PyObject, bool> matches, Func<PyObject[], PyDict?, PyObject> construct)
    {
        var type = new PyType(name, matches, construct);
        All[name] = type;
        return type;
    }

    /// <summary><c>object</c>.</summary>
    public static PyType Object { get; } = Define("object", static _ => true, static (_, _) => PyNone.Instance);

    /// <summary><c>type</c>.</summary>
    public static PyType Type { get; } = Define(
        "type",
        static value => value is PyType or PyClass or PyExceptionType,
        static (arguments, keywords) =>
        {
            // `type(x)` asks what something is; `type(name, bases, namespace)` makes a new
            // class, which is how a decorator or a factory builds one at runtime.
            if (arguments.Length == 3)
            {
                PyBuiltinFunction.RejectKeywords("type", keywords);
                // The namespace is copied, so later edits to the caller's dict do not
                // reach into the class.
                var members = new PyDict();

                if (arguments[2] is PyDict namespaceDict)
                {
                    foreach (var (key, value) in namespaceDict.Entries)
                    {
                        members.Set(key, value);
                    }
                }

                return new PyClass(arguments[0].Display(), members);
            }

            if (arguments.Length is not (0 or 1))
            {
                throw new PyRaise(PyErrors.TypeError("type() takes 1 or 3 arguments"));
            }

            PyBuiltinFunction.RejectKeywords("type", keywords);
            return arguments.Length > 0 ? Of(arguments[0]) : Object;
        });

    /// <summary><c>NoneType</c>.</summary>
    public static PyType NoneType { get; } = Define(
        "NoneType", static value => value is PyNone, static (_, _) => PyNone.Instance);

    /// <summary><c>bool</c>.</summary>
    public static PyType Bool { get; } = Define(
        "bool",
        static value => value is PyBool,
        static (arguments, _) => PyBool.Of(arguments.Length > 0 && arguments[0].IsTruthy()));

    /// <summary><c>int</c>. <c>bool</c> is a subtype, so a bool is an int.</summary>
    public static PyType Int { get; } = Define(
        "int", static value => value is PyInt, static (arguments, keywords) => Builtins.Conversions.ToInt(arguments, keywords));

    /// <summary><c>float</c>.</summary>
    public static PyType Float { get; } = Define(
        "float", static value => value is PyFloat, static (arguments, _) => Builtins.Conversions.ToFloat(arguments));

    /// <summary><c>str</c>.</summary>
    public static PyType Str { get; } = Define(
        "str",
        static value => value is PyStr,
        static (arguments, _) => new PyStr(arguments.Length == 0 ? string.Empty : arguments[0].Display()));

    /// <summary><c>bytes</c>.</summary>
    public static PyType Bytes { get; } = Define(
        "bytes",
        static value => value is PyBytes,
        static (arguments, keywords) => Builtins.Conversions.ToBytes(arguments, keywords));

    /// <summary><c>list</c>.</summary>
    public static PyType List { get; } = Define(
        "list",
        static value => value is PyList,
        static (arguments, _) => new PyList(
            arguments.Length == 0 ? [] : VirtualMachine.RequireIterable(arguments[0]).ToList()));

    /// <summary><c>tuple</c>.</summary>
    public static PyType Tuple { get; } = Define(
        "tuple",
        static value => value is PyTuple,
        static (arguments, _) => new PyTuple(
            arguments.Length == 0 ? [] : VirtualMachine.RequireIterable(arguments[0]).ToList()));

    /// <summary><c>set</c>.</summary>
    public static PyType Set { get; } = Define(
        "set",
        static value => value is PySet { IsFrozen: false },
        static (arguments, _) => new PySet(
            arguments.Length == 0 ? [] : VirtualMachine.RequireIterable(arguments[0])));

    /// <summary><c>frozenset</c>.</summary>
    public static PyType FrozenSet { get; } = Define(
        "frozenset",
        static value => value is PySet { IsFrozen: true },
        static (arguments, _) => new PySet(
            arguments.Length == 0 ? [] : VirtualMachine.RequireIterable(arguments[0]))
        {
            IsFrozen = true,
        });

    /// <summary><c>dict</c>.</summary>
    public static PyType Dict { get; } = Define("dict", static value => value is PyDict, BuildDict);

    /// <summary><c>range</c>.</summary>
    public static PyType Range { get; } = Define("range", static value => value is PyRange, BuildRange);

    /// <summary><c>slice</c>.</summary>
    public static PyType Slice { get; } = Define(
        "slice",
        static value => value is PySlice,
        static (arguments, _) => arguments.Length switch
        {
            0 => throw new PyRaise(PyErrors.TypeError("slice expected at least 1 argument, got 0")),
            1 => new PySlice(null, arguments[0], null),
            2 => new PySlice(arguments[0], arguments[1], null),
            _ => new PySlice(arguments[0], arguments[1], arguments[2]),
        });

    /// <summary><c>function</c>.</summary>
    public static PyType Function { get; } = Define(
        "function",
        static value => value is PyFunction,
        static (_, _) => throw new PyRaise(PyErrors.TypeError("cannot create 'function' instances")));

    /// <summary><c>generator</c>.</summary>
    public static PyType Generator { get; } = Define(
        "generator",
        static value => value is PyGenerator,
        static (_, _) => throw new PyRaise(PyErrors.TypeError("cannot create 'generator' instances")));

    /// <summary>The type object for a value.</summary>
    public static PyObject Of(PyObject value) => value switch
    {
        // A user-defined instance reports its own class, and a class reports `type`.
        PyInstance instance => instance.Class,
        PyException exception => exception.ExceptionType,
        PyClass or PyType or PyExceptionType => Type,
        PyBool => Bool,
        PyInt => Int,
        PyFloat => Float,
        PyStr => Str,
        PyBytes => Bytes,
        PyList => List,
        PyCounter => PyCounter.Type,
        PyDeque => PyDeque.Type,
        // A named tuple reports its own class, which is what makes `type(p) is Point` hold.
        PyNamedTuple named => named.Type,
        PyTuple => Tuple,
        PySet { IsFrozen: true } => FrozenSet,
        PySet => Set,
        PyDict => Dict,
        PyRange => Range,
        PySlice => Slice,
        PyNone => NoneType,
        PyFunction => Function,
        PyGenerator => Generator,
        // A host record names its own type; the type object is made on demand and cached,
        // so `type(x) is type(y)` holds for two records of the same shape.
        PyDataclass record => Named(record.Name),
        // Anything else answers with a type object named for itself, made on demand and
        // cached, so `type(x) is type(y)` holds for two values of the same host type.
        _ => Named(value.TypeName),
    };

    /// <summary>Gets, creating if needed, the type object for a host-supplied name.</summary>
    private static PyType Named(string name)
    {
        lock (All)
        {
            if (All.TryGetValue(name, out var known))
            {
                return known;
            }

            var type = new PyType(
                name,
                value => value.TypeName == name,
                static (_, _) =>
                throw new PyRaise(PyErrors.TypeError("cannot create instances of a host type")));

            All[name] = type;
            return type;
        }
    }

    /// <summary>
    /// Parses <c>bytes.fromhex</c>'s argument: hex digit pairs, optionally separated by
    /// spaces, but never split across one.
    /// </summary>
    internal static PyBytes FromHex(string text)
    {
        var bytes = new List<byte>();

        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                continue;
            }

            if (!Uri.IsHexDigit(text[i]))
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"non-hexadecimal number found in fromhex() arg at position {i}"));
            }

            if (i + 1 == text.Length)
            {
                throw new PyRaise(PyErrors.ValueError(
                    "fromhex() arg must contain an even number of hexadecimal digits"));
            }

            if (!Uri.IsHexDigit(text[i + 1]))
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"non-hexadecimal number found in fromhex() arg at position {i + 1}"));
            }

            bytes.Add(Convert.ToByte(text.Substring(i, 2), 16));
            i++;
        }

        return new PyBytes([.. bytes]);
    }

    /// <summary>True when <paramref name="value"/> is an instance of <paramref name="type"/>.</summary>
    public static bool IsInstance(PyObject value, PyObject type)
    {
        switch (type)
        {
            case PyTuple tuple:
                return tuple.Items.Any(item => IsInstance(value, item));

            case PyExceptionType exceptionType:
                return value is PyException exception && exception.IsInstanceOf(exceptionType);

            case PyClass pyClass:
                return value is PyInstance instance && ReferenceEquals(instance.Class, pyClass);

            case PyNamedTupleType namedTuple:
                return value is PyNamedTuple named && ReferenceEquals(named.Type, namedTuple);

            case PyType builtin:
                // `bool` is a subtype of `int`, so a bool satisfies both.
                if (ReferenceEquals(builtin, Int) && value is PyBool)
                {
                    return true;
                }

                return builtin.Matches(value);

            // A value that is plainly not a type — a string, a number, a container — is a
            // mistake in the call rather than a false answer. Anything else that acts as a
            // constructor is left to answer for itself.
            case PyStr or PyInt or PyFloat or PyList or PyDict or PySet or PyNone or PyBytes:
                throw new PyRaise(PyErrors.TypeError(
                    "isinstance() arg 2 must be a type, a tuple of types, or a union"));

            default:
                return false;
        }
    }

    private static PyObject BuildDict(PyObject[] arguments, PyDict? keywords)
    {
        var dict = new PyDict();

        if (arguments.Length > 0)
        {
            if (arguments[0] is PyDict source)
            {
                foreach (var (key, value) in source.Entries)
                {
                    dict.Set(key, value);
                }
            }
            else
            {
                foreach (var pair in VirtualMachine.RequireIterable(arguments[0]))
                {
                    var items = VirtualMachine.RequireIterable(pair).ToList();

                    // Each element must be a two-item sequence; anything else is the
                    // error CPython reports rather than a host index failure.
                    if (items.Count != 2)
                    {
                        throw new PyRaise(PyErrors.ValueError(
                            $"dictionary update sequence element #0 has length {items.Count}; 2 is required"));
                    }

                    dict.Set(items[0], items[1]);
                }
            }
        }

        if (keywords is not null)
        {
            foreach (var (key, value) in keywords.Entries)
            {
                dict.Set(key, value);
            }
        }

        return dict;
    }

    private static PyObject BuildRange(PyObject[] arguments, PyDict? keywords)
    {
        _ = keywords;

        return arguments.Length switch
        {
            1 => new PyRange(0, Bound(arguments[0]), 1),
            2 => new PyRange(Bound(arguments[0]), Bound(arguments[1]), 1),
            3 => new PyRange(Bound(arguments[0]), Bound(arguments[1]), Bound(arguments[2])),
            _ => throw new PyRaise(PyErrors.TypeError($"range expected at most 3 arguments, got {arguments.Length}")),
        };

        static System.Numerics.BigInteger Bound(PyObject value) => value is PyInt integer
            ? integer.Value
            : throw new PyRaise(PyErrors.TypeError(
                $"'{value.TypeName}' object cannot be interpreted as an integer"));
    }
}
