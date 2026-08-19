using System.Numerics;
using Monty.Runtime;

namespace Monty.Modules;

/// <summary>The <c>math</c> module.</summary>
public static class MathModule
{
    /// <summary>
    /// Rejects an infinite input, which the trigonometric functions have no answer for.
    /// </summary>
    private static double Bounded(double value) =>
        double.IsInfinity(value)
            ? throw new PyRaise(PyErrors.ValueError($"expected a finite input, got {Describe(value)}"))
            : value;

    /// <summary>Rejects an input outside -1..1, which <c>asin</c> and <c>acos</c> require.</summary>
    private static double InUnitRange(double value) =>
        value is < -1 or > 1
            ? throw new PyRaise(PyErrors.ValueError(
                $"expected a number in range from -1 up to 1, got {Describe(value)}"))
            : value;

    /// <summary>Reports an overflow when a finite input produced an infinite result.</summary>
    private static double Finite(double result) =>
        double.IsInfinity(result)
            ? throw new PyRaise(new PyException(PyExceptionType.OverflowError, "math range error"))
            : result;

    private static string Describe(double value) =>
        double.IsPositiveInfinity(value) ? "inf"
        : double.IsNegativeInfinity(value) ? "-inf"
        : PyFloat.Format(value);

    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create()
    {
        var module = new PyModuleObject("math");

        module.Add("pi", new PyFloat(Math.PI));
        module.Add("e", new PyFloat(Math.E));
        module.Add("tau", new PyFloat(Math.Tau));
        module.Add("inf", new PyFloat(double.PositiveInfinity));
        module.Add("nan", new PyFloat(double.NaN));

        Unary(module, "sqrt", value =>
            value < 0 ? throw new PyRaise(PyErrors.ValueError("math domain error")) : Math.Sqrt(value));

        // CPython checks the domain before calling libm and reports precisely what was
        // wrong, so a script sees ValueError rather than a silent nan flowing onward.
        Unary(module, "exp", value => Finite(Math.Exp(value)));
        Unary(module, "sin", value => Math.Sin(Bounded(value)));
        Unary(module, "cos", value => Math.Cos(Bounded(value)));
        Unary(module, "tan", value => Math.Tan(Bounded(value)));
        Unary(module, "asin", value => Math.Asin(InUnitRange(value)));
        Unary(module, "acos", value => Math.Acos(InUnitRange(value)));
        Unary(module, "atan", Math.Atan);
        Unary(module, "sinh", value => Finite(Math.Sinh(value)));
        Unary(module, "cosh", value => Finite(Math.Cosh(value)));
        Unary(module, "tanh", Math.Tanh);
        Unary(module, "degrees", static value => value * 180 / Math.PI);
        Unary(module, "radians", static value => value * Math.PI / 180);

        Unary(module, "log2", value =>
            value <= 0 ? throw new PyRaise(PyErrors.ValueError("math domain error")) : Math.Log2(value));

        Unary(module, "log10", value =>
            value <= 0 ? throw new PyRaise(PyErrors.ValueError("math domain error")) : Math.Log10(value));

        module.Add("log", arguments =>
        {
            var value = ToDouble(arguments[0]);

            if (value <= 0)
            {
                throw new PyRaise(PyErrors.ValueError("math domain error"));
            }

            if (arguments.Length <= 1)
            {
                return new PyFloat(Math.Log(value));
            }

            var logarithm = Math.Log(ToDouble(arguments[1]));

            // `log(x, 1)` divides by log(1), which is zero.
            if (logarithm == 0)
            {
                throw new PyRaise(new PyException(PyExceptionType.ZeroDivisionError, "division by zero"));
            }

            return new PyFloat(Math.Log(value) / logarithm);
        });

        module.Add("atan2", static arguments =>
            new PyFloat(Math.Atan2(ToDouble(arguments[0]), ToDouble(arguments[1]))));

        module.Add("hypot", static arguments =>
            new PyFloat(Math.Sqrt(arguments.Sum(a => ToDouble(a) * ToDouble(a)))));

        module.Add("pow", static arguments =>
        {
            var result = Math.Pow(ToDouble(arguments[0]), ToDouble(arguments[1]));

            // A negative base with a fractional exponent has no real result.
            return double.IsNaN(result) && !double.IsNaN(ToDouble(arguments[0]))
                ? throw new PyRaise(PyErrors.ValueError("math domain error"))
                : new PyFloat(Finite(result));
        });

        // floor, ceil and trunc return int in Python 3, not float — and an infinite or
        // NaN input is an OverflowError or ValueError, not a host crash.
        module.Add("floor", static arguments => ToInteger(Math.Floor(ToDouble(arguments[0])), "floor"));
        module.Add("ceil", static arguments => ToInteger(Math.Ceiling(ToDouble(arguments[0])), "ceil"));
        module.Add("trunc", static arguments => ToInteger(Math.Truncate(ToDouble(arguments[0])), "trunc"));

        module.Add("fabs", static arguments => new PyFloat(Math.Abs(ToDouble(arguments[0]))));

        module.Add("fmod", static arguments =>
        {
            var x = ToDouble(arguments[0]);
            var y = ToDouble(arguments[1]);

            if (double.IsInfinity(x) || (y == 0 && !double.IsNaN(x)))
            {
                throw new PyRaise(PyErrors.ValueError("math domain error"));
            }

            // C's fmod keeps the sign of the dividend, which is what `%` does in C#.
            return new PyFloat(x % y);
        });

        module.Add("isnan", static arguments => PyBool.Of(double.IsNaN(ToDouble(arguments[0]))));
        module.Add("isinf", static arguments => PyBool.Of(double.IsInfinity(ToDouble(arguments[0]))));
        module.Add("isfinite", static arguments => PyBool.Of(double.IsFinite(ToDouble(arguments[0]))));

        module.Add("factorial", static arguments =>
        {
            var n = RequireInt(arguments[0]);

            if (n < 0)
            {
                throw new PyRaise(PyErrors.ValueError("factorial() not defined for negative values"));
            }

            if (n > 20_000)
            {
                throw new PyRaise(new PyException(PyExceptionType.OverflowError, "factorial argument too large"));
            }

            var result = BigInteger.One;

            for (var i = BigInteger.One; i <= n; i++)
            {
                result *= i;
            }

            return new PyInt(result);
        });

        module.Add("gcd", static arguments =>
        {
            var result = BigInteger.Zero;

            foreach (var argument in arguments)
            {
                result = BigInteger.GreatestCommonDivisor(result, BigInteger.Abs(RequireInt(argument)));
            }

            return new PyInt(result);
        });

        module.Add("lcm", static arguments =>
        {
            var result = BigInteger.One;

            foreach (var argument in arguments)
            {
                var value = BigInteger.Abs(RequireInt(argument));

                if (value.IsZero)
                {
                    return new PyInt(0);
                }

                result = result / BigInteger.GreatestCommonDivisor(result, value) * value;
            }

            return new PyInt(result);
        });

        module.Add("isqrt", static arguments =>
        {
            var value = RequireInt(arguments[0]);

            if (value < 0)
            {
                throw new PyRaise(PyErrors.ValueError("isqrt() argument must be nonnegative"));
            }

            // Newton's method, since a double round-trip loses precision on large values.
            if (value < 2)
            {
                return new PyInt(value);
            }

            var guess = value;
            var next = (guess + 1) / 2;

            while (next < guess)
            {
                guess = next;
                next = (guess + (value / guess)) / 2;
            }

            return new PyInt(guess);
        });

        module.Add("comb", static arguments =>
        {
            var n = RequireInt(arguments[0]);
            var k = RequireInt(arguments[1]);

            if (k < 0 || n < 0)
            {
                throw new PyRaise(PyErrors.ValueError("n and k must be non-negative integers"));
            }

            if (k > n)
            {
                return new PyInt(0);
            }

            var result = BigInteger.One;
            k = BigInteger.Min(k, n - k);

            for (BigInteger i = 0; i < k; i++)
            {
                result = result * (n - i) / (i + 1);
            }

            return new PyInt(result);
        });

        module.Add("prod", static arguments =>
        {
            PyObject total = new PyInt(1);

            foreach (var item in VirtualMachine.RequireIterable(arguments[0]))
            {
                total = Operators.Binary("*", total, item);
            }

            return total;
        });

        module.Add("fsum", static arguments =>
            new PyFloat(VirtualMachine.RequireIterable(arguments[0]).Sum(ToDouble)));

        module.Add("copysign", static arguments =>
            new PyFloat(Math.CopySign(ToDouble(arguments[0]), ToDouble(arguments[1]))));

        module.Add("isclose", static (arguments, keywords) =>
        {
            var a = ToDouble(arguments[0]);
            var b = ToDouble(arguments[1]);

            var relative = Keyword(keywords, "rel_tol") ?? 1e-9;
            var absolute = Keyword(keywords, "abs_tol") ?? 0.0;

            return PyBool.Of(Math.Abs(a - b) <= Math.Max(relative * Math.Max(Math.Abs(a), Math.Abs(b)), absolute));
        });

        return module;
    }

    private static double? Keyword(PyDict? keywords, string name) =>
        keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) ? ToDouble(value) : null;

    private static void Unary(PyModuleObject module, string name, Func<double, double> implementation) =>
        module.Add(name, arguments => new PyFloat(implementation(ToDouble(arguments[0]))));

    private static PyObject ToInteger(double value, string function)
    {
        if (double.IsNaN(value))
        {
            throw new PyRaise(PyErrors.ValueError($"cannot convert float NaN to integer"));
        }

        if (double.IsInfinity(value))
        {
            throw new PyRaise(new PyException(
                PyExceptionType.OverflowError, "cannot convert float infinity to integer"));
        }

        _ = function;
        return new PyInt(new BigInteger(value));
    }

    private static double ToDouble(PyObject value) => value switch
    {
        PyInt integer => (double)integer.Value,
        PyFloat number => number.Value,
        _ => throw new PyRaise(PyErrors.TypeError($"must be real number, not {value.TypeName}")),
    };

    private static BigInteger RequireInt(PyObject value) => value switch
    {
        PyInt integer => integer.Value,
        _ => throw new PyRaise(PyErrors.TypeError($"'{value.TypeName}' object cannot be interpreted as an integer")),
    };
}
