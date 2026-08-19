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
                if (arguments.Length < minimum || arguments.Length > maximum)
                {
                    throw new PyRaise(PyErrors.TypeError(minimum == maximum
                        ? $"{name}() takes exactly {(minimum == 1 ? "one argument" : minimum + " arguments")} ({arguments.Length} given)"
                        : arguments.Length < minimum
                            ? $"{name} expected at least {minimum} argument{(minimum == 1 ? string.Empty : "s")}, got {arguments.Length}"
                            : $"{name} expected at most {maximum} argument{(maximum == 1 ? string.Empty : "s")}, got {arguments.Length}"));
                }

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
            var separator = Keyword(keywords, "sep")?.Display() ?? " ";
            var end = Keyword(keywords, "end")?.Display() ?? "\n";
            machine.Write(string.Join(separator, arguments.Select(static a => a.Display())) + end);
            return PyNone.Instance;
        });

        DefineArity("len", 1, 1, arguments =>
            new PyInt(arguments[0].Length()
                ?? throw new PyRaise(PyErrors.TypeError($"object of type '{arguments[0].TypeName}' has no len()"))));

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
            var sequences = arguments.Select(static a => VirtualMachine.RequireIterable(a).ToList()).ToList();

            if (sequences.Count == 0)
            {
                return new PyIterator([]);
            }

            // Zipping stops at the shortest sequence.
            var length = sequences.Min(static s => s.Count);
            var rows = new List<PyObject>(length);

            for (var i = 0; i < length; i++)
            {
                rows.Add(new PyTuple(sequences.Select(sequence => sequence[i]).ToList()));
            }

            return new PyIterator(rows);
        });

        DefineArity("map", 2, int.MaxValue, arguments =>
        {
            var function = arguments[0];
            var sequences = arguments.Skip(1).Select(static a => VirtualMachine.RequireIterable(a).ToList()).ToList();
            var length = sequences.Count == 0 ? 0 : sequences.Min(static s => s.Count);
            var results = new List<PyObject>(length);

            for (var i = 0; i < length; i++)
            {
                results.Add(machine.Call(function, [.. sequences.Select(sequence => sequence[i])]));
            }

            return new PyIterator(results);
        });

        DefineArity("filter", 2, 2, arguments =>
        {
            var predicate = arguments[0];
            var results = new List<PyObject>();

            foreach (var item in VirtualMachine.RequireIterable(arguments[1]))
            {
                // A None predicate filters on truthiness alone.
                var keep = predicate is PyNone ? item.IsTruthy() : machine.Call(predicate, [item]).IsTruthy();

                if (keep)
                {
                    results.Add(item);
                }
            }

            return new PyIterator(results);
        });

        Define("sorted", (arguments, keywords) =>
        {
            Arity.AtLeast("sorted", arguments, 1);
            return new PyList(Sorting.Sort(machine, VirtualMachine.RequireIterable(arguments[0]), keywords));
        });

        DefineArity("reversed", 1, 1, static arguments =>
        {
            var items = VirtualMachine.RequireIterable(arguments[0]).ToList();
            items.Reverse();
            return new PyIterator(items);
        });

        Define("sum", static (arguments, keywords) =>
        {
            var total = arguments.Length > 1 ? arguments[1] : Keyword(keywords, "start") ?? new PyInt(0);

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
            var digits = given is null or PyNone ? 0 : (int)RequireInt(given, "round");

            return number switch
            {
                PyInt integer when digits >= 0 => integer,
                // Python rounds half to even, unlike the usual half-away-from-zero.
                PyFloat value => given is null or PyNone
                    ? new PyInt(new BigInteger(Math.Round(value.Value, MidpointRounding.ToEven)))
                    : new PyFloat(Math.Round(value.Value, digits, MidpointRounding.ToEven)),
                PyInt integer => new PyInt(integer.Value),
                var other => throw new PyRaise(PyErrors.TypeError(
                    $"type {other.TypeName} doesn't define __round__ method")),
            };
        });

        DefineArity("divmod", 2, 2, static arguments => new PyTuple([
            Operators.Binary("//", arguments[0], arguments[1]),
            Operators.Binary("%", arguments[0], arguments[1]),
        ]));

        DefineArity("pow", 2, 3, static arguments =>
        {
            var result = Operators.Binary("**", arguments[0], arguments[1]);

            return arguments.Length > 2 ? Operators.Binary("%", result, arguments[2]) : result;
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

        DefineArity("iter", 1, 2, static arguments =>
        {
            if (arguments[0] is PyIterator or PyGenerator)
            {
                return arguments[0];
            }

            // A class whose `__iter__` returns self must come back unwrapped: `iter(c) is c`
            // is the contract every hand-written iterator relies on.
            if (arguments[0] is PyInstance instance && instance.Dunder("__iter__") is { } method)
            {
                return instance.Invoke(method, []);
            }

            return new PyIterator(VirtualMachine.RequireIterable(arguments[0]), IteratorName(arguments[0]));
        });

        DefineArity("next", 1, 2, static arguments =>
        {
            var value = arguments[0] switch
            {
                PyIterator iterator => iterator.Next(),
                PyGenerator generator => generator.Next(),
                var other => throw new PyRaise(PyErrors.TypeError($"'{other.TypeName}' object is not an iterator")),
            };

            if (value is not null)
            {
                return value;
            }

            // A default argument turns exhaustion into a value rather than a raise.
            return arguments.Length > 1 ? arguments[1] : throw new PyRaise(PyErrors.StopIteration());
        });

        DefineArity("getattr", 2, 3, arguments =>
        {
            var value = Runtime.Attributes.TryGet(machine, arguments[0], arguments[1].Display());

            if (value is not null)
            {
                return value;
            }

            return arguments.Length > 2
                ? arguments[2]
                : throw new PyRaise(PyErrors.AttributeError(arguments[0].TypeName, arguments[1].Display()));
        });

        DefineArity("setattr", 3, 3, static arguments =>
        {
            var name = arguments[1].Display();

            return arguments[0].SetAttribute(name, arguments[2])
                ? PyNone.Instance
                : throw new PyRaise(PyErrors.AttributeError(arguments[0].TypeName, name));
        });

        DefineArity("hasattr", 2, 2, arguments =>
            PyBool.Of(Runtime.Attributes.TryGet(machine, arguments[0], arguments[1].Display()) is not null));

        DefineArity("callable", 1, 1, static arguments => PyBool.Of(arguments[0] is PyCallable or PyExceptionType));

        DefineArity("format", 1, 2, static arguments =>
            new PyStr(StringFormatter.Format(arguments[0], arguments.Length > 1 ? arguments[1].Display() : string.Empty)));

        return builtins;
    }

    /// <summary>
    /// The type name of the iterator a value produces, which CPython derives from the
    /// container: <c>list_iterator</c>, <c>str_iterator</c>, and so on.
    /// </summary>
    private static string IteratorName(PyObject value) => value switch
    {
        PyList => "list_iterator",
        PyTuple => "tuple_iterator",
        PyStr => "str_iterator",
        PySet => "set_iterator",
        PyDict => "dict_keyiterator",
        PyRange => "range_iterator",
        PyBytes => "bytes_iterator",
        PyView view => view.TypeName + "_iterator",
        _ => "iterator",
    };

    private static PyObject? Keyword(PyDict? keywords, string name) =>
        keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) ? value : null;

    private static PyObject Extreme(VirtualMachine machine, PyObject[] arguments, PyDict? keywords, bool smallest)
    {
        // One iterable argument means "over its elements"; several mean "among them".
        var items = arguments.Length == 1
            ? VirtualMachine.RequireIterable(arguments[0]).ToList()
            : [.. arguments];

        if (items.Count == 0)
        {
            var fallback = Keyword(keywords, "default");

            return fallback
                ?? throw new PyRaise(PyErrors.ValueError(
                    $"{(smallest ? "min" : "max")}() iterable argument is empty"));
        }

        var key = Keyword(keywords, "key");
        var best = items[0];
        var bestKey = key is null ? best : machine.Call(key, [best]);

        foreach (var item in items.Skip(1))
        {
            var itemKey = key is null ? item : machine.Call(key, [item]);
            var comparison = itemKey.PyCompare(bestKey)
                ?? throw new PyRaise(PyErrors.TypeError(
                    $"'<' not supported between instances of '{itemKey.TypeName}' and '{bestKey.TypeName}'"));

            if (smallest ? comparison < 0 : comparison > 0)
            {
                best = item;
                bestKey = itemKey;
            }
        }

        return best;
    }

    private static PyStr Prefixed(BigInteger value, int radix, string prefix)
    {
        var digits = StringFormatterAccess.FormatRadix(BigInteger.Abs(value), radix);
        return new PyStr((value.Sign < 0 ? "-" : string.Empty) + prefix + digits);
    }

    internal static BigInteger RequireInt(PyObject value, string function) => value switch
    {
        PyInt integer => integer.Value,
        PyFloat number => new BigInteger(Math.Truncate(number.Value)),
        _ => throw new PyRaise(PyErrors.TypeError(
            $"'{value.TypeName}' object cannot be interpreted as an integer")),
    };
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
    public static PyObject ToInt(PyObject[] arguments)
    {
        if (arguments.Length == 0)
        {
            return new PyInt(0);
        }

        var radix = arguments.Length > 1 ? (int)BuiltinNamespace.RequireInt(arguments[1], "int") : 10;

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

            case PyStr text:
            {
                var trimmed = text.Value.Trim().Replace("_", string.Empty, StringComparison.Ordinal);

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

        // A literal may carry a radix prefix, which must agree with the requested base.
        if (text.Length > 2 && text[0] == '0')
        {
            var prefix = char.ToLowerInvariant(text[1]);
            var prefixRadix = prefix switch { 'x' => 16, 'o' => 8, 'b' => 2, _ => 0 };

            if (prefixRadix != 0)
            {
                if (radix != 10 && radix != prefixRadix)
                {
                    return false;
                }

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

    /// <summary>Implements <c>bytes()</c>.</summary>
    public static PyObject ToBytes(PyObject[] arguments)
    {
        if (arguments.Length == 0)
        {
            return new PyBytes([]);
        }

        switch (arguments[0])
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

            case PyStr text when arguments.Length > 1:
                return new PyBytes(Encoding.UTF8.GetBytes(text.Value));

            case PyStr:
                throw new PyRaise(PyErrors.TypeError("string argument without an encoding"));

            default:
                return new PyBytes([.. VirtualMachine.RequireIterable(arguments[0])
                    .Select(static item => (byte)BuiltinNamespace.RequireInt(item, "bytes"))]);
        }
    }
}
