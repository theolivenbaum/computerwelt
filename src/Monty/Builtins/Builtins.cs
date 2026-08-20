using System.Globalization;
using System.Numerics;
using System.Text;
using Monty.Runtime;

namespace Monty.Builtins;

/// <summary>Builds the builtin namespace.</summary>
/// <remarks>
/// Only what Monty documents is present. A name that is absent here is absent from the
/// sandbox — there is no fallback to a host implementation, which is what makes the
/// vocabulary auditable.
/// </remarks>
public static class BuiltinNamespace
{
    /// <summary>Creates the builtin namespace for a machine.</summary>
    public static PyDict Create(VirtualMachine machine)
    {
        var builtins = new PyDict();

        void Define(string name, Func<PyObject[], PyDict?, PyObject> implementation) =>
            builtins.Set(new PyStr(name), new PyBuiltinFunction(name, implementation));

        void DefinePositional(string name, Func<PyObject[], PyObject> implementation) =>
            builtins.Set(new PyStr(name), new PyBuiltinFunction(name, implementation));

        // Builtins that dereference a fixed number of arguments declare it, so a missing
        // one becomes a TypeError rather than a host index error escaping to the script.
        void DefineArity(string name, int minimum, int maximum, Func<PyObject[], PyObject> implementation) =>
            builtins.Set(new PyStr(name), new PyBuiltinFunction(name, arguments =>
            {
                if (minimum == maximum)
                {
                    Arity.Exact(name, arguments, minimum);
                }
                else
                {
                    Arity.Between(name, arguments, minimum, maximum);
                }

                return implementation(arguments);
            }));

        // A few builtins spell their fixed arity the other way round; see `Arity`.
        void DefineExact(string name, int count, Func<PyObject[], PyObject> implementation) =>
            builtins.Set(new PyStr(name), new PyBuiltinFunction(name, arguments =>
            {
                Arity.ExactCount(name, arguments, count);
                return implementation(arguments);
            }));

        builtins.Set(new PyStr("None"), PyNone.Instance);
        builtins.Set(new PyStr("True"), PyBool.True);
        builtins.Set(new PyStr("False"), PyBool.False);
        builtins.Set(new PyStr("Ellipsis"), PyEllipsis.Instance);

        // Assertions are always enabled in the sandbox, so `__debug__` is always true.
        builtins.Set(new PyStr("__debug__"), PyBool.True);
        builtins.Set(new PyStr("NotImplemented"), NotImplementedSingleton.Instance);

        foreach (var (name, type) in PyExceptionType.Registry)
        {
            // `JSONDecodeError` is defined by the json module, not by the language.
            if (name == "JSONDecodeError")
            {
                continue;
            }

            builtins.Set(new PyStr(name), type);
        }

        // Built-in types are first-class objects, not constructor functions, so that
        // `type(x) is int` and `isinstance(x, int)` both hold.
        foreach (var (name, type) in TypeRegistry.All)
        {
            builtins.Set(new PyStr(name), type);
        }

        Define("print", (arguments, keywords) =>
        {
            var separator = Text(Keyword(keywords, "sep"), "sep") ?? " ";
            var end = Text(Keyword(keywords, "end"), "end") ?? "\n";
            machine.Write(string.Join(separator, arguments.Select(static a => a.Display())) + end);
            return PyNone.Instance;
        });

        DefineArity("len", 1, 1, arguments => arguments[0] switch
        {
            // A range knows its length without materialising it, so `len` reaches as far as
            // an ssize_t does rather than as far as a host int.
            PyRange range => new PyInt(range.LongCount),
            var value => new PyInt(value.Length()
                ?? throw new PyRaise(PyErrors.TypeError($"object of type '{value.TypeName}' has no len()"))),
        });

        DefineArity("repr", 1, 1, static arguments => new PyStr(arguments[0].Repr()));











        DefineArity("enumerate", 1, 2, static arguments =>
        {
            var start = arguments.Length > 1 ? RequireInt(arguments[1], "enumerate") : BigInteger.Zero;
            var items = new List<PyObject>();
            var index = start;

            foreach (var item in VirtualMachine.RequireIterable(arguments[0]))
            {
                items.Add(new PyTuple([new PyInt(index++), item]));
            }

            return new PyIterator(items);
        });

        DefinePositional("zip", static arguments =>
        {
            // Lazy, so zipping against an endless source such as `itertools.count()`
            // terminates: the shortest side ends it.
            var sources = arguments.Select(static a => VirtualMachine.RequireIterable(a).GetEnumerator()).ToList();
            var spent = sources.Count == 0;

            return new PyIterator(
                () =>
                {
                    if (spent)
                    {
                        return null;
                    }

                    var row = new List<PyObject>(sources.Count);

                    foreach (var source in sources)
                    {
                        if (!source.MoveNext())
                        {
                            spent = true;
                            return null;
                        }

                        row.Add(source.Current);
                    }

                    return new PyTuple(row);
                },
                "zip");
        });

        builtins.Set(new PyStr("map"), new PyBuiltinFunction("map", arguments =>
        {
            // `map` words its own arity error, and words it as a sentence.
            if (arguments.Length < 2)
            {
                throw new PyRaise(PyErrors.TypeError("map() must have at least two arguments."));
            }

            var function = arguments[0];
            var sources = arguments.Skip(1)
                .Select(static a => VirtualMachine.RequireIterable(a).GetEnumerator()).ToList();

            var spent = false;

            return new PyIterator(
                () =>
                {
                    if (spent)
                    {
                        return null;
                    }

                    var row = new List<PyObject>(sources.Count);

                    foreach (var source in sources)
                    {
                        if (!source.MoveNext())
                        {
                            spent = true;
                            return null;
                        }

                        row.Add(source.Current);
                    }

                    return machine.Call(function, [.. row]);
                },
                "map");
        }));

        DefineArity("filter", 2, 2, arguments =>
        {
            var predicate = arguments[0];
            var source = VirtualMachine.RequireIterable(arguments[1]).GetEnumerator();

            return new PyIterator(
                () =>
                {
                    while (source.MoveNext())
                    {
                        // A None predicate filters on truthiness alone.
                        var keep = predicate is PyNone
                            ? source.Current.IsTruthy()
                            : machine.Call(predicate, [source.Current]).IsTruthy();

                        if (keep)
                        {
                            return source.Current;
                        }
                    }

                    return null;
                },
                "filter");
        });

        Define("sorted", (arguments, keywords) =>
        {
            Arity.AtLeast("sorted", arguments, 1);
            return new PyList(Sorting.Sort(machine, VirtualMachine.RequireIterable(arguments[0]), keywords));
        });

        DefineArity("reversed", 1, 1, static arguments =>
        {
            // Being iterable is not enough: reversing needs a sequence with a length and
            // an order, which a one-shot iterator and an unordered set do not have.
            if (arguments[0] is not (PyList or PyTuple or PyStr or PyBytes or PyRange or PyDict or PyDeque)
                && (arguments[0] as PyInstance)?.Dunder("__reversed__") is null)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"'{arguments[0].TypeName}' object is not reversible"));
            }

            if (arguments[0] is PyInstance instance && instance.Dunder("__reversed__") is { } reverse)
            {
                return instance.Invoke(reverse, []);
            }

            var items = VirtualMachine.RequireIterable(arguments[0]).ToList();
            items.Reverse();
            return new PyIterator(items);
        });

        Define("sum", static (arguments, keywords) =>
        {
            var total = arguments.Length > 1 ? arguments[1] : Keyword(keywords, "start") ?? new PyInt(0);

            // Summing strings is almost always a mistake and quadratic when it is not, so
            // CPython refuses and points at the right tool instead.
            if (total is PyStr)
            {
                throw new PyRaise(PyErrors.TypeError("sum() can't sum strings [use ''.join(seq) instead]"));
            }

            if (total is PyBytes)
            {
                throw new PyRaise(PyErrors.TypeError("sum() can't sum bytes [use b''.join(seq) instead]"));
            }

            foreach (var item in VirtualMachine.RequireIterable(arguments[0]))
            {
                total = Operators.Binary("+", total, item);
            }

            return total;
        });

        Define("min", (arguments, keywords) => Extreme(machine, arguments, keywords, smallest: true));
        Define("max", (arguments, keywords) => Extreme(machine, arguments, keywords, smallest: false));

        DefineArity("abs", 1, 1, static arguments => arguments[0] switch
        {
            PyInt integer => new PyInt(BigInteger.Abs(integer.Value)),
            PyFloat number => new PyFloat(Math.Abs(number.Value)),
            var other => throw new PyRaise(PyErrors.TypeError($"bad operand type for abs(): '{other.TypeName}'")),
        });

        DefineArity("all", 1, 1, static arguments =>
            PyBool.Of(VirtualMachine.RequireIterable(arguments[0]).All(static item => item.IsTruthy())));

        DefineArity("any", 1, 1, static arguments =>
            PyBool.Of(VirtualMachine.RequireIterable(arguments[0]).Any(static item => item.IsTruthy())));

        // Both of round's parameters are keyword-capable in CPython.
        Define("round", static (arguments, keywords) =>
        {
            var number = arguments.Length > 0 ? arguments[0] : Keyword(keywords, "number")
                ?? throw new PyRaise(PyErrors.TypeError("round() missing required argument 'number'"));

            var given = arguments.Length > 1 ? arguments[1] : Keyword(keywords, "ndigits");
            var requested = given is null or PyNone ? BigInteger.Zero : RequireInt(given, "round");

            // A precision wider than any representable number saturates: asking for 10**30
            // digits leaves the value alone, and -10**30 flattens it to zero.
            var digits = requested > 400 ? 400 : requested < -400 ? -400 : (int)requested;

            // A non-finite float has no integral form, but with an explicit precision it
            // is simply returned: rounding it changes nothing.
            if (number is PyFloat { Value: var raw } special && !double.IsFinite(raw))
            {
                if (given is not (null or PyNone))
                {
                    return special;
                }

                throw new PyRaise(double.IsNaN(raw)
                    ? PyErrors.ValueError("cannot convert float NaN to integer")
                    : new PyException(
                        PyExceptionType.OverflowError, "cannot convert float infinity to integer"));
            }

            return number switch
            {
                PyInt integer when digits >= 0 => integer,
                PyInt when digits <= -400 => new PyInt(BigInteger.Zero),
                // Python rounds half to even, unlike the usual half-away-from-zero.
                // .NET only rounds to 15 decimal places; beyond that a double has no
                // digits left to lose, so the value is already its own rounding.
                PyFloat value => given is null or PyNone
                    ? new PyInt(new BigInteger(Math.Round(value.Value, MidpointRounding.ToEven)))
                    : digits > 15 ? value
                    : digits >= 0 ? new PyFloat(Math.Round(value.Value, digits, MidpointRounding.ToEven))
                    // A float rounded away to nothing keeps its sign: -1.5 at a huge
                    // negative precision is -0.0, not 0.0.
                    : new PyFloat(Math.CopySign(
                        (double)RoundToMultiple(
                            new BigInteger(value.Value), BigInteger.Pow(10, Math.Min(-digits, 400))),
                        value.Value)),
                PyInt integer => new PyInt(RoundToMultiple(integer.Value, BigInteger.Pow(10, -digits))),
                var other => throw new PyRaise(PyErrors.TypeError(
                    $"type {other.TypeName} doesn't define __round__ method")),
            };
        });

        DefineArity("divmod", 2, 2, static arguments =>
        {
            // The pair is `//` and `%`, but a bad operand names `divmod()` rather than the
            // operator it happened to try first.
            if (arguments[0] is not (PyInt or PyFloat) || arguments[1] is not (PyInt or PyFloat))
            {
                throw new PyRaise(PyErrors.TypeError(
                    "unsupported operand type(s) for divmod(): "
                    + $"'{arguments[0].TypeName}' and '{arguments[1].TypeName}'"));
            }

            return new PyTuple([
                Operators.Binary("//", arguments[0], arguments[1]),
                Operators.Binary("%", arguments[0], arguments[1]),
            ]);
        });

        DefineArity("pow", 2, 3, static arguments =>
        {
            if (arguments.Length <= 2)
            {
                return Operators.Binary("**", arguments[0], arguments[1]);
            }

            // The three-argument form is modular exponentiation, which is defined for
            // integers only.
            if (arguments.Any(static a => a is not PyInt))
            {
                throw new PyRaise(PyErrors.TypeError(
                    "pow() 3rd argument not allowed unless all arguments are integers"));
            }

            var modulus = ((PyInt)arguments[2]).Value;

            if (modulus.IsZero)
            {
                throw new PyRaise(PyErrors.ValueError("pow() 3rd argument cannot be 0"));
            }

            var exponent = ((PyInt)arguments[1]).Value;

            if (exponent.Sign < 0)
            {
                throw new PyRaise(PyErrors.ValueError(
                    "pow() 2nd argument cannot be negative when 3rd argument specified"));
            }

            var power = BigInteger.ModPow(((PyInt)arguments[0]).Value, exponent, modulus);

            // The result takes the modulus's sign, as `%` does.
            return new PyInt(power.Sign != 0 && power.Sign != modulus.Sign ? power + modulus : power);
        });

        DefineArity("hash", 1, 1, static arguments => new PyInt(arguments[0].PyHash()));

        DefineArity("id", 1, 1, static arguments =>
            new PyInt(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(arguments[0])));

        DefineArity("chr", 1, 1, static arguments =>
            new PyStr(char.ConvertFromUtf32((int)RequireInt(arguments[0], "chr"))));

        DefineArity("ord", 1, 1, static arguments =>
        {
            var text = arguments[0].Display();

            return text.Length == 1 || char.IsSurrogatePair(text, 0)
                ? new PyInt(char.ConvertToUtf32(text, 0))
                : throw new PyRaise(PyErrors.TypeError(
                    $"ord() expected a character, but string of length {text.Length} found"));
        });

        DefineArity("bin", 1, 1, static arguments =>
            Prefixed(RequireInt(arguments[0], "bin"), 2, "0b"));

        DefineArity("hex", 1, 1, static arguments =>
            Prefixed(RequireInt(arguments[0], "hex"), 16, "0x"));

        DefineArity("oct", 1, 1, static arguments =>
            Prefixed(RequireInt(arguments[0], "oct"), 8, "0o"));

        DefineArity("isinstance", 2, 2, static arguments =>
            PyBool.Of(TypeRegistry.IsInstance(arguments[0], arguments[1])));

        DefineArity("issubclass", 2, 2, static arguments => PyBool.Of(
            (arguments[0], arguments[1]) switch
            {
                (PyExceptionType left, PyExceptionType right) => left.IsSubclassOf(right),
                (PyType left, PyType right) => ReferenceEquals(left, right)
                    || (ReferenceEquals(left, TypeRegistry.Bool) && ReferenceEquals(right, TypeRegistry.Int))
                    || ReferenceEquals(right, TypeRegistry.Object),
                (PyClass left, PyClass right) => ReferenceEquals(left, right),
                _ => false,
            }));

        DefineArity("iter", 1, 2, arguments =>
        {
            // `iter(callable, sentinel)` builds an iterator that calls until the result
            // equals the sentinel; the sentinel itself is never yielded.
            if (arguments.Length == 2)
            {
                if (arguments[0] is not PyCallable and not PyFunction and not PyBoundMethod)
                {
                    throw new PyRaise(PyErrors.TypeError("iter(v, w): v must be callable"));
                }

                var (callable, sentinel) = (arguments[0], arguments[1]);
                var stopped = false;

                return new PyIterator(
                    () =>
                    {
                        if (stopped)
                        {
                            return null;
                        }

                        PyObject value;

                        try
                        {
                            value = machine.Call(callable, []);
                        }
                        catch (PyRaise raise)
                            when (raise.Exception.ExceptionType == PyExceptionType.StopIteration)
                        {
                            stopped = true;
                            return null;
                        }

                        if (!PyObject.SameOrEqual(value, sentinel))
                        {
                            return value;
                        }

                        stopped = true;
                        return null;
                    },
                    "callable_iterator");
            }

            if (arguments[0] is PyIterator or PyGenerator)
            {
                return arguments[0];
            }

            // A class whose `__iter__` returns self must come back unwrapped: `iter(c) is c`
            // is the contract every hand-written iterator relies on.
            if (arguments[0] is PyInstance instance && instance.Dunder("__iter__") is { } method)
            {
                var iterator = instance.Invoke(method, []);

                // Whatever `__iter__` hands back must itself be an iterator, or the loop
                // that trusts it would fail somewhere far less informative.
                return iterator is PyIterator or PyGenerator
                    || (iterator is PyInstance produced && produced.Dunder("__next__") is not null)
                    ? iterator
                    : throw new PyRaise(PyErrors.TypeError(
                        $"iter() returned non-iterator of type '{iterator.TypeName}'"));
            }

            return new PyIterator(VirtualMachine.RequireIterable(arguments[0]), IteratorName(arguments[0]));
        });

        DefineArity("next", 1, 2, static arguments =>
        {
            PyObject? value;

            switch (arguments[0])
            {
                case PyIterator iterator:
                    value = iterator.Next();
                    break;

                case PyGenerator generator:
                    value = generator.Next();
                    break;

                // A hand-written iterator is any object with `__next__`; its StopIteration
                // is what a default argument rescues.
                case PyInstance instance when instance.Dunder("__next__") is { } advance:
                    try
                    {
                        value = instance.Invoke(advance, []);
                    }
                    catch (PyRaise raise)
                        when (raise.Exception.ExceptionType == PyExceptionType.StopIteration
                            && arguments.Length > 1)
                    {
                        return arguments[1];
                    }

                    break;

                default:
                    throw new PyRaise(PyErrors.TypeError(
                        $"'{arguments[0].TypeName}' object is not an iterator"));
            }

            if (value is not null)
            {
                return value;
            }

            // A default argument turns exhaustion into a value rather than a raise.
            return arguments.Length > 1 ? arguments[1] : throw new PyRaise(PyErrors.StopIteration());
        });

        DefineArity("getattr", 2, 3, arguments =>
        {
            var name = Arity.AttributeName(arguments[1]);
            var value = Runtime.Attributes.TryGet(machine, arguments[0], name);

            if (value is not null)
            {
                return value;
            }

            return arguments.Length > 2
                ? arguments[2]
                : throw new PyRaise(PyErrors.AttributeError(arguments[0].TypeName, name));
        });

        DefineExact("setattr", 3, static arguments =>
        {
            var name = Arity.AttributeName(arguments[1]);

            return arguments[0].SetAttribute(name, arguments[2])
                ? PyNone.Instance
                : throw new PyRaise(new PyException(
                    PyExceptionType.AttributeError,
                    $"'{arguments[0].TypeName}' object has no attribute '{name}' "
                    + "and no __dict__ for setting new attributes"));
        });

        DefineExact("hasattr", 2, arguments =>
            PyBool.Of(Runtime.Attributes.TryGet(machine, arguments[0], Arity.AttributeName(arguments[1])) is not null));

        DefineArity("callable", 1, 1, static arguments => PyBool.Of(arguments[0] is PyCallable or PyExceptionType));

        DefineArity("format", 1, 2, static arguments =>
            new PyStr(StringFormatter.Format(arguments[0], arguments.Length > 1 ? arguments[1].Display() : string.Empty)));

        return builtins;
    }

    /// <summary>
    /// The type name of the iterator a value produces, which CPython derives from the
    /// container: <c>list_iterator</c>, <c>str_iterator</c>, and so on.
    /// </summary>
    /// <summary>
    /// Reads a keyword that must be a string, or null when it was absent or None.
    /// </summary>
    private static string? Text(PyObject? value, string name) => value switch
    {
        null or PyNone => null,
        PyStr text => text.Value,
        _ => throw new PyRaise(PyErrors.TypeError(
            $"{name} must be None or a string, not {value.TypeName}")),
    };

    private static string IteratorName(PyObject value) => value switch
    {
        PyList => "list_iterator",
        PyTuple => "tuple_iterator",
        // CPython has a separate, faster iterator for a string that is all ASCII.
        PyStr text => text.Value.All(char.IsAscii) ? "str_ascii_iterator" : "str_iterator",
        PySet => "set_iterator",
        PyDict => "dict_keyiterator",
        PyRange => "range_iterator",
        PyBytes => "bytes_iterator",
        // `dict_keys` iterates as a `dict_keyiterator`, not a `dict_keys_iterator`.
        PyView view => view.TypeName switch
        {
            "dict_keys" => "dict_keyiterator",
            "dict_items" => "dict_itemiterator",
            "dict_values" => "dict_valueiterator",
            var other => other + "_iterator",
        },
        _ => "iterator",
    };

    private static PyObject? Keyword(PyDict? keywords, string name) =>
        keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) ? value : null;

    private static bool IsNaN(PyObject value) => value is PyFloat number && double.IsNaN(number.Value);

    private static PyObject Extreme(VirtualMachine machine, PyObject[] arguments, PyDict? keywords, bool smallest)
    {
        var name = smallest ? "min" : "max";

        foreach (var (keyword, _) in keywords?.Entries ?? [])
        {
            if (keyword.Display() is not ("key" or "default"))
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{name}() got an unexpected keyword argument '{keyword.Display()}'"));
            }
        }

        if (arguments.Length == 0)
        {
            throw new PyRaise(PyErrors.TypeError($"{name} expected at least 1 argument, got 0"));
        }

        // One iterable argument means "over its elements"; several mean "among them".
        var items = arguments.Length == 1
            ? VirtualMachine.RequireIterable(arguments[0]).ToList()
            : [.. arguments];

        if (items.Count == 0)
        {
            var fallback = Keyword(keywords, "default");

            return fallback ?? throw new PyRaise(PyErrors.ValueError($"{name}() iterable argument is empty"));
        }

        // An explicit `key=None` asks for the identity, not for None to be called.
        var key = Keyword(keywords, "key") is { } given and not PyNone ? given : null;
        var best = items[0];
        var bestKey = key is null ? best : machine.Call(key, [best]);

        foreach (var item in items.Skip(1))
        {
            var itemKey = key is null ? item : machine.Call(key, [item]);

            // A NaN orders against nothing, so it never displaces the running best — the
            // first element wins by default, exactly as CPython's loop leaves it. A pair
            // with no ordering for any other reason is still an error.
            var comparison = itemKey.PyCompare(bestKey)
                ?? (IsNaN(itemKey) || IsNaN(bestKey)
                    ? 0
                    : throw new PyRaise(PyErrors.TypeError(
                        $"'<' not supported between instances of '{itemKey.TypeName}' and '{bestKey.TypeName}'")));

            if (smallest ? comparison < 0 : comparison > 0)
            {
                best = item;
                bestKey = itemKey;
            }
        }

        return best;
    }

    /// <summary>
    /// Rounds an integer to the nearest multiple, halves going to the even multiple.
    /// </summary>
    /// <remarks>
    /// This is what <c>round(1250, -2)</c> asks for. Doing it in floating point would lose
    /// the exactness that makes an integer round trip.
    /// </remarks>
    private static BigInteger RoundToMultiple(BigInteger value, BigInteger multiple)
    {
        var quotient = BigInteger.DivRem(value, multiple, out var remainder);

        if (remainder.IsZero)
        {
            return value;
        }

        var doubled = BigInteger.Abs(remainder) * 2;
        var rounds = doubled > multiple || (doubled == multiple && !quotient.IsEven);

        return rounds ? (quotient + value.Sign) * multiple : quotient * multiple;
    }

    private static PyStr Prefixed(BigInteger value, int radix, string prefix)
    {
        var digits = StringFormatterAccess.FormatRadix(BigInteger.Abs(value), radix);
        return new PyStr((value.Sign < 0 ? "-" : string.Empty) + prefix + digits);
    }

    /// <summary>
    /// Reads an argument that must be an integer.
    /// </summary>
    /// <remarks>
    /// A float is refused rather than truncated: <c>hex(1.5)</c> is a mistake in the call,
    /// and silently rounding it would hide one.
    /// </remarks>
    internal static BigInteger RequireInt(PyObject value, string function)
    {
        _ = function;

        return value is PyInt integer
            ? integer.Value
            : throw new PyRaise(PyErrors.TypeError(
                $"'{value.TypeName}' object cannot be interpreted as an integer"));
    }
}

/// <summary>The <c>NotImplemented</c> singleton returned by unsupported dunder operations.</summary>
internal sealed class NotImplementedSingleton : PyObject
{
    private NotImplementedSingleton()
    {
    }

    public static NotImplementedSingleton Instance { get; } = new();

    public override string TypeName => "NotImplementedType";

    public override string Repr() => "NotImplemented";

    /// <inheritdoc />
    /// <remarks>
    /// Truth-testing NotImplemented is almost always a comparison whose result was never
    /// checked, so CPython refuses rather than quietly answering true.
    /// </remarks>
    public override bool IsTruthy() =>
        throw new PyRaise(PyErrors.TypeError(
            "NotImplemented should not be used in a boolean context"));

    public override bool PyEquals(PyObject other) => other is NotImplementedSingleton;
}

/// <summary>Exposes the radix formatter without widening its visibility further.</summary>
internal static class StringFormatterAccess
{
    public static string FormatRadix(BigInteger value, int radix)
    {
        if (value.IsZero)
        {
            return "0";
        }

        const string Alphabet = "0123456789abcdef";
        var digits = new StringBuilder();

        while (!value.IsZero)
        {
            digits.Insert(0, Alphabet[(int)(value % radix)]);
            value /= radix;
        }

        return digits.ToString();
    }
}

/// <summary>The <c>int()</c>, <c>float()</c> and <c>bytes()</c> conversions.</summary>
/// <remarks>Shared with <see cref="TypeRegistry"/>, which exposes them as type objects.</remarks>
public static class Conversions
{
    /// <summary>Implements <c>int()</c>.</summary>
    public static PyObject ToInt(PyObject[] arguments, PyDict? keywords = null)
    {
        if (arguments.Length == 0)
        {
            return new PyInt(0);
        }

        var given = arguments.Length > 1 ? arguments[1]
            : keywords is not null && keywords.TryGetValue(new PyStr("base"), out var named) ? named
            : null;

        var radix = given is null ? 10 : (int)BuiltinNamespace.RequireInt(given, "int");

        switch (arguments[0])
        {
            case PyBool flag:
                return new PyInt(flag.Value ? 1 : 0);

            case PyInt integer:
                return integer;

            case PyFloat number:
                if (double.IsNaN(number.Value) || double.IsInfinity(number.Value))
                {
                    throw new PyRaise(PyErrors.ValueError(
                        $"cannot convert float {(double.IsNaN(number.Value) ? "NaN" : "infinity")} to integer"));
                }

                return new PyInt(new BigInteger(Math.Truncate(number.Value)));

            case PyBytes raw:
                // A bytes literal is read as the ASCII text it spells.
                return ToInt([new PyStr(System.Text.Encoding.UTF8.GetString(raw.Value)), .. arguments[1..]], keywords);

            case PyStr text:
            {
                var trimmed = text.Value.Trim().Replace("_", string.Empty, StringComparison.Ordinal);

                // A well-formed decimal literal that is merely too long gets the digit-limit
                // error, and gets it before any number is built; a malformed one gets the
                // ordinary complaint, however long it is.
                if (radix is 10 or 0
                    && trimmed.TrimStart('-', '+') is { Length: > PyInt.MaxStringDigits } digits
                    && digits.All(char.IsAsciiDigit))
                {
                    throw new PyRaise(PyErrors.ValueError(
                        $"Exceeds the limit ({PyInt.MaxStringDigits} digits) for integer string conversion: "
                        + $"value has {digits.Length} digits"));
                }

                if (TryParseRadix(trimmed, radix, out var parsed))
                {
                    return new PyInt(parsed);
                }

                throw new PyRaise(PyErrors.ValueError(
                    $"invalid literal for int() with base {radix}: {text.Repr()}"));
            }

            default:
                throw new PyRaise(PyErrors.TypeError(
                    $"int() argument must be a string, a bytes-like object or a real number, not '{arguments[0].TypeName}'"));
        }
    }

    private static bool TryParseRadix(string text, int radix, out BigInteger value)
    {
        value = BigInteger.Zero;

        if (text.Length == 0)
        {
            return false;
        }

        var negative = text[0] == '-';

        if (text[0] is '-' or '+')
        {
            text = text[1..];
        }

        // Base 0 asks for the prefix to decide, defaulting to decimal.
        var detect = radix == 0;

        if (detect)
        {
            radix = 10;
        }

        // A literal may carry a radix prefix, which must agree with the requested base.
        if (text.Length > 2 && text[0] == '0')
        {
            var prefix = char.ToLowerInvariant(text[1]);
            var prefixRadix = prefix switch { 'x' => 16, 'o' => 8, 'b' => 2, _ => 0 };

            // A prefix is only a prefix when it agrees with the base being read. In base
            // 16 `b` is a digit, so `int('0b10', 16)` is 0xb10, not binary 10.
            if (prefixRadix != 0 && (detect || radix == 10 || radix == prefixRadix))
            {
                radix = prefixRadix;
                text = text[2..];
            }
        }

        if (text.Length == 0)
        {
            return false;
        }

        foreach (var c in text)
        {
            var digit = char.IsAsciiDigit(c) ? c - '0'
                : char.IsAsciiLetter(c) ? char.ToLowerInvariant(c) - 'a' + 10
                : -1;

            if (digit < 0 || digit >= radix)
            {
                return false;
            }

            value = (value * radix) + digit;
        }

        value = negative ? -value : value;
        return true;
    }

    /// <summary>Implements <c>float()</c>.</summary>
    public static PyObject ToFloat(PyObject[] arguments)
    {
        if (arguments.Length == 0)
        {
            return new PyFloat(0);
        }

        switch (arguments[0])
        {
            case PyBool flag:
                return new PyFloat(flag.Value ? 1 : 0);

            case PyInt integer:
                return new PyFloat((double)integer.Value);

            case PyFloat number:
                return number;

            case PyStr text:
            {
                var trimmed = text.Value.Trim().Replace("_", string.Empty, StringComparison.Ordinal);

                var special = trimmed.ToLowerInvariant() switch
                {
                    "inf" or "+inf" or "infinity" or "+infinity" => double.PositiveInfinity,
                    "-inf" or "-infinity" => double.NegativeInfinity,
                    "nan" or "+nan" or "-nan" => double.NaN,
                    _ => (double?)null,
                };

                if (special is { } value)
                {
                    return new PyFloat(value);
                }

                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    return new PyFloat(parsed);
                }

                throw new PyRaise(PyErrors.ValueError($"could not convert string to float: {text.Repr()}"));
            }

            default:
                throw new PyRaise(PyErrors.TypeError(
                    $"float() argument must be a string or a real number, not '{arguments[0].TypeName}'"));
        }
    }

    /// <summary>Reads a bytes() argument that must be a string.</summary>
    private static string? Named(PyObject? value, string name) => value switch
    {
        null => null,
        PyStr text => text.Value,

        // The argument clinic spells a lone None as "None", not "NoneType".
        PyNone => throw new PyRaise(PyErrors.TypeError(
            $"bytes() argument '{name}' must be str, not None")),
        _ => throw new PyRaise(PyErrors.TypeError(
            $"bytes() argument '{name}' must be str, not {value.TypeName}")),
    };

    /// <summary>Implements <c>bytes()</c>.</summary>
    /// <remarks>
    /// Every parameter is positional-or-keyword, so <c>bytes(source='x', encoding='utf-8')</c>
    /// works — and giving one both ways is an error naming the parameter and its position.
    /// </remarks>
    public static PyObject ToBytes(PyObject[] arguments, PyDict? keywords)
    {
        string[] names = ["source", "encoding", "errors"];

        // The clinic counts positionals and keywords together before it inspects either.
        if (arguments.Length + (keywords?.Count ?? 0) > names.Length)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"bytes() takes at most {names.Length} arguments "
                + $"({arguments.Length + (keywords?.Count ?? 0)} given)"));
        }

        var given = new PyObject?[names.Length];

        for (var i = 0; i < names.Length; i++)
        {
            given[i] = arguments.Length > i ? arguments[i] : null;
        }

        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            var position = Array.IndexOf(names, key.Display());

            if (position < 0)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"bytes() got an unexpected keyword argument '{key.Display()}'"));
            }

            if (given[position] is not null)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"argument for bytes() given by name ('{names[position]}') "
                    + $"and position ({position + 1})"));
            }

            given[position] = value;
        }

        // An encoding or an error handler only means anything for a str source, and the
        // encoding is checked first.
        if (given[0] is not PyStr)
        {
            if (given[1] is not null)
            {
                throw new PyRaise(PyErrors.TypeError("encoding without a string argument"));
            }

            if (given[2] is not null)
            {
                throw new PyRaise(PyErrors.TypeError("errors without a string argument"));
            }
        }

        if (given[0] is not { } source)
        {
            return new PyBytes([]);
        }

        arguments = [.. given.TakeWhile(static value => value is not null).Select(static value => value!)];

        switch (source)
        {
            case PyBytes bytes:
                return bytes;

            case PyInt size:
            {
                if (size.Value < 0)
                {
                    throw new PyRaise(PyErrors.ValueError("negative count"));
                }

                if (size.Value > 100_000_000)
                {
                    throw new PyRaise(new PyException(PyExceptionType.OverflowError, "cannot fit 'int' into a size"));
                }

                return new PyBytes(new byte[(int)size.Value]);
            }

            case PyStr text when given[1] is not null:
                return new PyBytes(Codecs.Encode(
                    text.Value,
                    Named(given[1], "encoding")!,
                    Named(given[2], "errors") ?? "strict"));

            case PyStr:
                throw new PyRaise(PyErrors.TypeError("string argument without an encoding"));

            default:
                return new PyBytes([.. VirtualMachine.RequireIterable(source)
                    .Select(static item => (byte)BuiltinNamespace.RequireInt(item, "bytes"))]);
        }
    }
}
