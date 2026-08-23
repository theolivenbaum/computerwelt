using System.Numerics;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

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

    /// <summary>
    /// Reports a result that grew past the range of a double, but lets an infinity that
    /// was already in the input through: <c>exp(inf)</c> is <c>inf</c>, while
    /// <c>exp(1000)</c> overflows.
    /// </summary>
    private static double Grow(double input, double result) =>
        double.IsFinite(input) ? Finite(result) : result;

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
        Unary(module, "exp", value => Grow(value, Math.Exp(value)));
        Unary(module, "sin", value => Math.Sin(Bounded(value)));
        Unary(module, "cos", value => Math.Cos(Bounded(value)));
        Unary(module, "tan", value => Math.Tan(Bounded(value)));
        Unary(module, "asin", value => Math.Asin(InUnitRange(value)));
        Unary(module, "acos", value => Math.Acos(InUnitRange(value)));
        Unary(module, "atan", Math.Atan);
        Unary(module, "sinh", value => Grow(value, Math.Sinh(value)));
        Unary(module, "cosh", value => Grow(value, Math.Cosh(value)));
        Unary(module, "tanh", Math.Tanh);
        Unary(module, "cbrt", Math.Cbrt);
        Unary(module, "expm1", value => Grow(value, Math.Exp(value) - 1));
        Unary(module, "log1p", static value =>
            value <= -1 ? throw new PyRaise(PyErrors.ValueError("math domain error")) : Math.Log(1 + value));
        Unary(module, "asinh", Math.Asinh);
        Unary(module, "acosh", static value =>
            value < 1 ? throw new PyRaise(PyErrors.ValueError("math domain error")) : Math.Acosh(value));
        Unary(module, "atanh", static value =>
            value is <= -1 or >= 1
                ? throw new PyRaise(PyErrors.ValueError("math domain error"))
                : Math.Atanh(value));
        Unary(module, "exp2", value => Grow(value, Math.Pow(2, value)));
        Unary(module, "erf", Erfs.Erf);
        Unary(module, "erfc", Erfs.Erfc);
        Unary(module, "gamma", static value => Grow(value, Gamma(value)));
        Unary(module, "lgamma", static value =>
        {
            // lgamma is defined where gamma overflows, so it uses the log-domain series
            // rather than the logarithm of a gamma that would already be infinite.
            if (double.IsInfinity(value))
            {
                return double.PositiveInfinity;
            }

            if (value <= 0 && double.IsInteger(value))
            {
                throw new PyRaise(PyErrors.ValueError("math domain error"));
            }

            return Finite(value < 0.5
                ? Math.Log(Math.Abs(Math.PI / Math.Sin(Math.PI * value))) - LogGamma(1 - value)
                : LogGamma(value));
        });
        Unary(module, "ulp", static value =>
            double.IsNaN(value) ? value
            : double.IsInfinity(value) ? double.PositiveInfinity
            : Math.BitIncrement(Math.Abs(value)) - Math.Abs(value));

        module.Add("nextafter", static arguments =>
        {
            var (from, towards) = (ToDouble(arguments[0]), ToDouble(arguments[1]));

            return new PyFloat(double.IsNaN(from) || double.IsNaN(towards) ? double.NaN
                : from < towards ? Math.BitIncrement(from)
                : from > towards ? Math.BitDecrement(from)
                : towards);
        });

        module.Add("remainder", static arguments =>
        {
            var (value, divisor) = (ToDouble(arguments[0]), ToDouble(arguments[1]));

            // An infinite dividend or a zero divisor has no remainder; only a nan input
            // may produce a nan result.
            return double.IsNaN(value) || double.IsNaN(divisor) ? new PyFloat(double.NaN)
                : double.IsInfinity(value) || divisor == 0
                    ? throw new PyRaise(PyErrors.ValueError("math domain error"))
                    : new PyFloat(Math.IEEERemainder(value, divisor));
        });

        module.Add("ldexp", static arguments =>
        {
            var value = ToDouble(arguments[0]);
            var exponent = RequireInt(arguments[1]);

            // Scaling by 2**e in one step would overflow for a large e even when the
            // result is representable, so the shift is applied in bounded steps.
            var result = value;

            for (var remaining = BigInteger.Abs(exponent); remaining > 0 && double.IsFinite(result) && result != 0;)
            {
                var step = (int)BigInteger.Min(remaining, 1000);
                result *= Math.Pow(2, exponent.Sign * step);
                remaining -= step;
            }

            return new PyFloat(Grow(value, result));
        });

        // `frexp` and `modf` split a float into the two halves of its representation.
        module.Add("frexp", static arguments =>
        {
            var value = ToDouble(arguments[0]);

            if (value == 0 || double.IsNaN(value) || double.IsInfinity(value))
            {
                return new PyTuple([new PyFloat(value), PyInt.From(0)]);
            }

            var exponent = (int)Math.Floor(Math.Log2(Math.Abs(value))) + 1;
            var mantissa = value / Math.Pow(2, exponent);

            // Rounding in the log can put the mantissa just outside [0.5, 1).
            while (Math.Abs(mantissa) >= 1)
            {
                mantissa /= 2;
                exponent++;
            }

            while (Math.Abs(mantissa) < 0.5)
            {
                mantissa *= 2;
                exponent--;
            }

            return new PyTuple([new PyFloat(mantissa), PyInt.From(exponent)]);
        });

        module.Add("modf", static arguments =>
        {
            var value = ToDouble(arguments[0]);

            // An infinity has no fractional part, but the zero it yields keeps its sign.
            if (double.IsInfinity(value))
            {
                return new PyTuple([new PyFloat(double.IsNegative(value) ? -0.0 : 0.0), new PyFloat(value)]);
            }

            if (double.IsNaN(value))
            {
                return new PyTuple([new PyFloat(value), new PyFloat(value)]);
            }

            // A zero is its own integral and fractional part, sign included; subtracting
            // would turn -0.0 into +0.0.
            if (value == 0)
            {
                return new PyTuple([new PyFloat(value), new PyFloat(value)]);
            }

            var whole = Math.Truncate(value);
            return new PyTuple([new PyFloat(value - whole), new PyFloat(whole)]);
        });

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

            var basis = ToDouble(arguments[1]);

            // The base has the same domain as the value, and is checked the same way.
            if (basis <= 0)
            {
                throw new PyRaise(PyErrors.ValueError("math domain error"));
            }

            var logarithm = Math.Log(basis);

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
            var (value, exponent) = (ToDouble(arguments[0]), ToDouble(arguments[1]));

            // Zero to a negative power is a pole, not an overflow.
            if (value == 0 && exponent < 0)
            {
                throw new PyRaise(PyErrors.ValueError("math domain error"));
            }

            var result = Math.Pow(value, exponent);

            // A negative base with a fractional exponent has no real result.
            return double.IsNaN(result) && !double.IsNaN(value)
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

            // A nan on either side wins over the domain check: fmod(inf, nan) is nan.
            if (double.IsNaN(x) || double.IsNaN(y))
            {
                return new PyFloat(double.NaN);
            }

            if (double.IsInfinity(x) || y == 0)
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

            return PyInt.From(result);
        });

        module.Add("gcd", static arguments =>
        {
            var result = BigInteger.Zero;

            foreach (var argument in arguments)
            {
                result = BigInteger.GreatestCommonDivisor(result, BigInteger.Abs(RequireInt(argument)));
            }

            return PyInt.From(result);
        });

        module.Add("lcm", static arguments =>
        {
            var result = BigInteger.One;

            foreach (var argument in arguments)
            {
                var value = BigInteger.Abs(RequireInt(argument));

                if (value.IsZero)
                {
                    return PyInt.From(0);
                }

                result = result / BigInteger.GreatestCommonDivisor(result, value) * value;
            }

            return PyInt.From(result);
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
                return PyInt.From(value);
            }

            var guess = value;
            var next = (guess + 1) / 2;

            while (next < guess)
            {
                guess = next;
                next = (guess + (value / guess)) / 2;
            }

            return PyInt.From(guess);
        });

        module.Add("perm", static arguments =>
        {
            var n = RequireInt(arguments[0]);
            var k = arguments.Length > 1 && arguments[1] is not PyNone ? RequireInt(arguments[1]) : n;

            if (k < 0 || n < 0)
            {
                throw new PyRaise(PyErrors.ValueError("n and k must be non-negative integers"));
            }

            if (k > n)
            {
                return PyInt.From(0);
            }

            var permutations = BigInteger.One;

            for (BigInteger i = 0; i < k; i++)
            {
                permutations *= n - i;
            }

            return PyInt.From(permutations);
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
                return PyInt.From(0);
            }

            var result = BigInteger.One;
            k = BigInteger.Min(k, n - k);

            for (BigInteger i = 0; i < k; i++)
            {
                result = result * (n - i) / (i + 1);
            }

            return PyInt.From(result);
        });

        module.Add("prod", static arguments =>
        {
            PyObject total = PyInt.From(1);

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
            if (arguments.Length > 2)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"isclose() takes at most 2 positional arguments ({arguments.Length} given)"));
            }

            // `a` and `b` may be given by name, but never twice.
            var a = ToDouble(Operand(arguments, keywords, 0, "a"));
            var b = ToDouble(Operand(arguments, keywords, 1, "b"));

            var relative = Keyword(keywords, "rel_tol") ?? 1e-9;
            var absolute = Keyword(keywords, "abs_tol") ?? 0.0;

            if (relative < 0 || absolute < 0)
            {
                throw new PyRaise(PyErrors.ValueError("tolerances must be non-negative"));
            }

            foreach (var (key, _) in keywords?.Entries ?? [])
            {
                if (key.Display() is not ("a" or "b" or "rel_tol" or "abs_tol"))
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"isclose() got an unexpected keyword argument '{key.Display()}'"));
                }
            }

            // An infinity is close only to the same infinity; subtracting would give nan
            // or inf and compare wrongly against any tolerance.
            if (double.IsInfinity(a) || double.IsInfinity(b))
            {
                return PyBool.Of(a.Equals(b));
            }

            return PyBool.Of(Math.Abs(a - b) <= Math.Max(relative * Math.Max(Math.Abs(a), Math.Abs(b)), absolute));
        });

        return module;
    }

    private static double? Keyword(PyDict? keywords, string name) =>
        keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) ? ToDouble(value) : null;

    /// <summary>
    /// The error function and its complement, as the FDLIBM rational approximations glibc
    /// uses.
    /// </summary>
    /// <remarks>
    /// .NET has no <c>erf</c>, and the fixtures compare results for exact equality against
    /// CPython — which calls the platform's <c>erf</c>. A different approximation of equal
    /// accuracy still differs in the last bit, so this is the same algorithm rather than an
    /// equivalent one: a polynomial in four ranges, with the tail expressed through
    /// <c>exp</c>.
    /// </remarks>
    private static class Erfs
    {
        private const double Erx = 8.45062911510467529297e-01;
        private const double Efx = 1.28379167095512586316e-01;
        private const double Efx8 = 1.02703333676410069053e+00;
        private const double Tiny = 1e-300;

        // |x| < 0.84375
        private static readonly double[] PP =
        [
            1.28379167095512558561e-01, -3.25042107247001499370e-01, -2.84817495755985104766e-02,
            -5.77027029648944159157e-03, -2.37630166566501626084e-05,
        ];

        private static readonly double[] QQ =
        [
            3.97917223959155352819e-01, 6.50222499887672944485e-02, 5.08130628187576562776e-03,
            1.32494738004321644526e-04, -3.96022827877536812320e-06,
        ];

        // 0.84375 <= |x| < 1.25
        private static readonly double[] PA =
        [
            -2.36211856075265944077e-03, 4.14856118683748331666e-01, -3.72207876035701323847e-01,
            3.18346619901161753674e-01, -1.10894694282396677476e-01, 3.54783043256182359371e-02,
            -2.16637559486879084300e-03,
        ];

        private static readonly double[] QA =
        [
            1.06420880400844228286e-01, 5.40397917702171048937e-01, 7.18286544141962662868e-02,
            1.26171219808761642112e-01, 1.36370839120290507362e-02, 1.19844998467991074170e-02,
        ];



        /// <summary>The error function.</summary>
        public static double Erf(double x)
        {
            if (double.IsNaN(x))
            {
                return x;
            }

            if (double.IsInfinity(x))
            {
                return Math.Sign(x);
            }

            var absolute = Math.Abs(x);

            if (absolute < 0.84375)
            {
                if (absolute < 3.7252902984619141e-09)
                {
                    return x + (Efx * x);
                }

                var z = x * x;
                return x + (x * (Polynomial(PP, z) / (1 + (z * Polynomial(QQ, z)))));
            }

            if (absolute < 1.25)
            {
                var s = absolute - 1;
                var ratio = Polynomial(PA, s) / (1 + (s * Polynomial(QA, s)));
                return x >= 0 ? Erx + ratio : -Erx - ratio;
            }

            if (absolute >= 6)
            {
                return x >= 0 ? 1 - Tiny : Tiny - 1;
            }

            var tail = Complement(absolute);
            return x >= 0 ? 1 - tail : tail - 1;
        }

        /// <summary>The complementary error function.</summary>
        public static double Erfc(double x)
        {
            if (double.IsNaN(x))
            {
                return x;
            }

            if (double.IsInfinity(x))
            {
                return x > 0 ? 0 : 2;
            }

            var absolute = Math.Abs(x);

            if (absolute < 0.84375)
            {
                if (absolute < 1.3877787807814457e-17)
                {
                    return 1 - x;
                }

                var z = x * x;
                var ratio = Polynomial(PP, z) / (1 + (z * Polynomial(QQ, z)));

                // Near zero the sum keeps its precision; further out the halves are
                // combined the other way round to avoid cancellation.
                if (x < 0.25)
                {
                    return 1 - (x + (x * ratio));
                }

                return 0.5 - (x - 0.5 + (x * ratio));
            }

            if (absolute < 1.25)
            {
                var s = absolute - 1;
                var ratio = Polynomial(PA, s) / (1 + (s * Polynomial(QA, s)));
                return x >= 0 ? 1 - Erx - ratio : 1 + Erx + ratio;
            }

            var tail = Complement(absolute);
            return x > 0 ? tail : 2 - tail;
        }

        /// <summary>
        /// The complement for a large argument, as the upper incomplete gamma
        /// <c>Q(1/2, x²)</c>, evaluated by Lentz's method.
        /// </summary>
        /// <remarks>
        /// FDLIBM covers this range with two more rational approximations; the continued
        /// fraction reaches the same double, and is short enough to read.
        /// </remarks>
        private static double Complement(double absolute)
        {
            var x = absolute * absolute;

            // Past this point the complement is smaller than the smallest double.
            if (absolute >= 28)
            {
                return 0;
            }

            const double Small = 1e-300;
            var a = 0.5;
            var b = x + 1 - a;
            var c = 1 / Small;
            var d = 1 / b;
            var fraction = d;

            for (var i = 1; i < 1000; i++)
            {
                var an = -i * (i - a);
                b += 2;
                d = (an * d) + b;
                c = b + (an / (Math.Abs(c) < Small ? Small : c));
                d = Math.Abs(d) < Small ? Small : d;
                c = Math.Abs(c) < Small ? Small : c;
                d = 1 / d;
                var delta = d * c;
                fraction *= delta;

                if (Math.Abs(delta - 1) < 1e-17)
                {
                    break;
                }
            }

            // ln Γ(1/2) is ln √π exactly.
            return Math.Exp(-x + (a * Math.Log(x)) - (0.5 * Math.Log(Math.PI))) * fraction;
        }

        /// <summary>Evaluates a polynomial in Horner form.</summary>
        private static double Polynomial(double[] coefficients, double x)
        {
            var result = coefficients[^1];

            for (var i = coefficients.Length - 2; i >= 0; i--)
            {
                result = coefficients[i] + (x * result);
            }

            return result;
        }
    }

    /// <summary>The natural logarithm of the gamma function, by the Lanczos series.</summary>
    private static double LogGamma(double value)
    {
        double[] coefficients =
        [
            76.18009172947146, -86.50532032941677, 24.01409824083091,
            -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5,
        ];

        var x = value;
        var y = value;
        var t = x + 5.5;
        t -= (x + 0.5) * Math.Log(t);
        var series = 1.000000000190015;

        foreach (var coefficient in coefficients)
        {
            series += coefficient / ++y;
        }

        return -t + Math.Log(2.5066282746310005 * series / x);
    }

    /// <summary>The gamma function, by the Lanczos approximation.</summary>
    private static double Gamma(double value)
    {
        if (double.IsNaN(value) || double.IsPositiveInfinity(value))
        {
            return value;
        }

        // The poles at zero and the negative integers, where the reflection formula would
        // divide by zero.
        if (double.IsNegativeInfinity(value) || (value <= 0 && double.IsInteger(value)))
        {
            throw new PyRaise(PyErrors.ValueError("math domain error"));
        }

        // The series is only approximate, but gamma of a small positive integer is a
        // factorial and scripts compare it exactly.
        if (double.IsInteger(value) && value <= 171)
        {
            var factorial = 1.0;

            for (var i = 2.0; i < value; i++)
            {
                factorial *= i;
            }

            return factorial;
        }

        if (value < 0.5)
        {
            // The reflection formula moves the argument into the range the series covers.
            return Math.PI / (Math.Sin(Math.PI * value) * Gamma(1 - value));
        }

        double[] coefficients =
        [
            676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012,
            9.9843695780195716e-6, 1.5056327351493116e-7,
        ];

        var x = value - 1;
        var sum = 0.99999999999980993;

        for (var i = 0; i < coefficients.Length; i++)
        {
            sum += coefficients[i] / (x + i + 1);
        }

        var t = x + coefficients.Length - 0.5;
        return Math.Sqrt(2 * Math.PI) * Math.Pow(t, x + 0.5) * Math.Exp(-t) * sum;
    }

    /// <summary>Reads an argument given either positionally or by name, never both.</summary>
    private static PyObject Operand(PyObject[] arguments, PyDict? keywords, int position, string name)
    {
        var named = keywords?.TryGetValue(new PyStr(name), out var value) == true ? value : null;

        if (arguments.Length > position)
        {
            return named is null
                ? arguments[position]
                : throw new PyRaise(PyErrors.TypeError(
                    $"isclose() got multiple values for argument '{name}'"));
        }

        return named ?? throw new PyRaise(PyErrors.TypeError(
            $"isclose() missing required argument '{name}' (pos {position + 1})"));
    }

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
        return PyInt.From(new BigInteger(value));
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
