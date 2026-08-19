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

        builtins.Set(new PyStr("None"), PyNone.Instance);
        builtins.Set(new PyStr("True"), PyBool.True);
        builtins.Set(new PyStr("False"), PyBool.False);

        foreach (var (name, type) in PyExceptionType.Registry)
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

        DefinePositional("len", arguments =>
            new PyInt(arguments[0].Length()
                ?? throw new PyRaise(PyErrors.TypeError($"object of type '{arguments[0].TypeName}' has no len()"))));

        DefinePositional("repr", static arguments => new PyStr(arguments[0].Repr()));

        DefinePositional("str", static arguments =>
            new PyStr(arguments.Length == 0 ? string.Empty : arguments[0].Display()));

        DefinePositional("bool", static arguments =>
            PyBool.Of(arguments.Length > 0 && arguments[0].IsTruthy()));

        DefinePositional("int", Conversions.ToInt);
        DefinePositional("float", Conversions.ToFloat);
        DefinePositional("bytes", Conversions.ToBytes);

        DefinePositional("list", static arguments => new PyList(
            arguments.Length == 0 ? [] : VirtualMachine.RequireIterable(arguments[0]).ToList()));

        DefinePositional("tuple", static arguments => new PyTuple(
            arguments.Length == 0 ? [] : VirtualMachine.RequireIterable(arguments[0]).ToList()));

        DefinePositional("set", static arguments => new PySet(
            arguments.Length == 0 ? [] : VirtualMachine.RequireIterable(arguments[0])));

        DefinePositional("frozenset", static arguments => new PySet(
            arguments.Length == 0 ? [] : VirtualMachine.RequireIterable(arguments[0])));

        Define("dict", static (arguments, keywords) =>
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
        });

        DefinePositional("range", static arguments => arguments.Length switch
        {
            1 => new PyRange(0, RequireInt(arguments[0], "range"), 1),
            2 => new PyRange(RequireInt(arguments[0], "range"), RequireInt(arguments[1], "range"), 1),
            3 => new PyRange(RequireInt(arguments[0], "range"), RequireInt(arguments[1], "range"), RequireInt(arguments[2], "range")),
            _ => throw new PyRaise(PyErrors.TypeError($"range expected at most 3 arguments, got {arguments.Length}")),
        });

        DefinePositional("enumerate", static arguments =>
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

        DefinePositional("map", arguments =>
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

        DefinePositional("filter", arguments =>
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
            new PyList(Sorting.Sort(machine, VirtualMachine.RequireIterable(arguments[0]), keywords)));

        DefinePositional("reversed", static arguments =>
        {
            var items = VirtualMachine.RequireIterable(arguments[0]).ToList();
            items.Reverse();
            return new PyIterator(items);
        });

        DefinePositional("sum", static arguments =>
        {
            PyObject total = arguments.Length > 1 ? arguments[1] : new PyInt(0);

            foreach (var item in VirtualMachine.RequireIterable(arguments[0]))
            {
                total = Operators.Binary("+", total, item);
            }

            return total;
        });

        Define("min", (arguments, keywords) => Extreme(machine, arguments, keywords, smallest: true));
        Define("max", (arguments, keywords) => Extreme(machine, arguments, keywords, smallest: false));

        DefinePositional("abs", static arguments => arguments[0] switch
        {
            PyInt integer => new PyInt(BigInteger.Abs(integer.Value)),
            PyFloat number => new PyFloat(Math.Abs(number.Value)),
            var other => throw new PyRaise(PyErrors.TypeError($"bad operand type for abs(): '{other.TypeName}'")),
        });

        DefinePositional("all", static arguments =>
            PyBool.Of(VirtualMachine.RequireIterable(arguments[0]).All(static item => item.IsTruthy())));

        DefinePositional("any", static arguments =>
            PyBool.Of(VirtualMachine.RequireIterable(arguments[0]).Any(static item => item.IsTruthy())));

        DefinePositional("round", static arguments =>
        {
            var digits = arguments.Length > 1 ? (int)RequireInt(arguments[1], "round") : 0;

            return arguments[0] switch
            {
                PyInt integer when digits >= 0 => integer,
                // Python rounds half to even, unlike the usual half-away-from-zero.
                PyFloat number => digits == 0 && arguments.Length < 2
                    ? new PyInt(new BigInteger(Math.Round(number.Value, MidpointRounding.ToEven)))
                    : new PyFloat(Math.Round(number.Value, digits, MidpointRounding.ToEven)),
                PyInt integer => new PyInt(integer.Value),
                var other => throw new PyRaise(PyErrors.TypeError(
                    $"type {other.TypeName} doesn't define __round__ method")),
            };
        });

        DefinePositional("divmod", static arguments => new PyTuple([
            Operators.Binary("//", arguments[0], arguments[1]),
            Operators.Binary("%", arguments[0], arguments[1]),
        ]));

        DefinePositional("pow", static arguments =>
        {
            var result = Operators.Binary("**", arguments[0], arguments[1]);

            return arguments.Length > 2 ? Operators.Binary("%", result, arguments[2]) : result;
        });

        DefinePositional("hash", static arguments => new PyInt(arguments[0].PyHash()));

        DefinePositional("id", static arguments =>
            new PyInt(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(arguments[0])));

        DefinePositional("chr", static arguments =>
            new PyStr(char.ConvertFromUtf32((int)RequireInt(arguments[0], "chr"))));

        DefinePositional("ord", static arguments =>
        {
            var text = arguments[0].Display();

            return text.Length == 1 || char.IsSurrogatePair(text, 0)
                ? new PyInt(char.ConvertToUtf32(text, 0))
                : throw new PyRaise(PyErrors.TypeError(
                    $"ord() expected a character, but string of length {text.Length} found"));
        });

        DefinePositional("bin", static arguments =>
            Prefixed(RequireInt(arguments[0], "bin"), 2, "0b"));

        DefinePositional("hex", static arguments =>
            Prefixed(RequireInt(arguments[0], "hex"), 16, "0x"));

        DefinePositional("oct", static arguments =>
            Prefixed(RequireInt(arguments[0], "oct"), 8, "0o"));

        DefinePositional("isinstance", static arguments => PyBool.Of(IsInstance(arguments[0], arguments[1])));

        DefinePositional("issubclass", static arguments => PyBool.Of(
            arguments[0] is PyExceptionType left && arguments[1] is PyExceptionType right && left.IsSubclassOf(right)));

        DefinePositional("type", static arguments => new PyStr(arguments[0].TypeName));

        DefinePositional("iter", static arguments =>
            arguments[0] is PyIterator or PyGenerator
                ? arguments[0]
                : new PyIterator(VirtualMachine.RequireIterable(arguments[0])));

        DefinePositional("next", static arguments =>
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

        DefinePositional("getattr", arguments =>
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

        DefinePositional("setattr", static arguments =>
        {
            var name = arguments[1].Display();

            return arguments[0].SetAttribute(name, arguments[2])
                ? PyNone.Instance
                : throw new PyRaise(PyErrors.AttributeError(arguments[0].TypeName, name));
        });

        DefinePositional("hasattr", arguments =>
            PyBool.Of(Runtime.Attributes.TryGet(machine, arguments[0], arguments[1].Display()) is not null));

        DefinePositional("callable", static arguments => PyBool.Of(arguments[0] is PyCallable or PyExceptionType));

        DefinePositional("format", static arguments =>
            new PyStr(StringFormatter.Format(arguments[0], arguments.Length > 1 ? arguments[1].Display() : string.Empty)));

        return builtins;
    }

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

    private static bool IsInstance(PyObject value, PyObject type)
    {
        if (type is PyTuple tuple)
        {
            return tuple.Items.Any(item => IsInstance(value, item));
        }

        if (type is PyExceptionType exceptionType)
        {
            return value is PyException exception && exception.IsInstanceOf(exceptionType);
        }

        if (type is PyClass pyClass)
        {
            return value is PyInstance instance && ReferenceEquals(instance.Class, pyClass);
        }

        // A builtin type is identified by the name bound to it, since built-in types are
        // not first-class objects here.
        var name = type is PyBuiltinFunction builtin ? builtin.Name : type.Display();

        return name switch
        {
            "int" => value is PyInt,
            "float" => value is PyFloat,
            "str" => value is PyStr,
            "bool" => value is PyBool,
            "list" => value is PyList,
            "dict" => value is PyDict,
            "tuple" => value is PyTuple,
            "set" or "frozenset" => value is PySet,
            "bytes" => value is PyBytes,
            "range" => value is PyRange,
            _ => false,
        };
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
internal static class Conversions
{
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
                return new PyBytes(new byte[size.ToIndex()]);

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
