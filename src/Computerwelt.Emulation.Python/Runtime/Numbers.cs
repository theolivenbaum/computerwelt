using System.Numerics;

namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// The numeric hash every number type shares.
/// </summary>
/// <remarks>
/// Python guarantees that numbers which compare equal hash equally, across types and at
/// every magnitude: <c>hash(2**100) == hash(2.0**100)</c> must hold, or the two could not
/// be the same dict key. CPython gets that by hashing the exact mathematical value modulo
/// the Mersenne prime 2**61-1 rather than by hashing a representation, and this reproduces
/// that arithmetic.
/// </remarks>
public static class Numbers
{
    /// <summary>The modulus, 2**61-1.</summary>
    private const long Modulus = (1L << 61) - 1;

    /// <summary>Hashes an integer.</summary>
    public static BigInteger Hash(BigInteger value)
    {
        var reduced = BigInteger.Remainder(value, Modulus);

        // -1 is reserved as the "error" hash in CPython, so a value that lands there is
        // reported as -2 instead.
        return reduced == BigInteger.MinusOne ? -2 : reduced;
    }

    /// <summary>Hashes a float, exactly as the equal integer would hash.</summary>
    public static BigInteger Hash(double value)
    {
        if (double.IsNaN(value))
        {
            // Every NaN hashes alike here; CPython uses the object's address, which a
            // sandbox has no business exposing.
            return 0;
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? 314159 : -314159;
        }

        var sign = value < 0 ? -1 : 1;
        var magnitude = Math.Abs(value);

        // Split into mantissa and exponent, then fold the mantissa into the modular
        // accumulator 28 bits at a time — the same loop CPython runs.
        var exponent = 0;

        while (magnitude != 0 && magnitude < 0.5)
        {
            magnitude *= 2;
            exponent--;
        }

        while (magnitude >= 1)
        {
            magnitude /= 2;
            exponent++;
        }

        BigInteger accumulated = 0;

        while (magnitude != 0)
        {
            accumulated = ((accumulated << 28) & Modulus) | (accumulated >> 33);
            magnitude *= 268435456.0;
            exponent -= 28;

            var digits = (long)magnitude;
            magnitude -= digits;
            accumulated += digits;

            if (accumulated >= Modulus)
            {
                accumulated -= Modulus;
            }
        }

        // Rotating by the exponent is multiplication by a power of two modulo 2**61-1,
        // because 2**61 is congruent to 1.
        var shift = exponent >= 0 ? exponent % 61 : 61 - (-exponent % 61);
        accumulated = ((accumulated << shift) & Modulus) | (accumulated >> (61 - shift));

        accumulated *= sign;
        return accumulated == BigInteger.MinusOne ? -2 : accumulated;
    }
}
