namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// Argument-count checks for built-in callables.
/// </summary>
/// <remarks>
/// Indexing an argument array directly would surface a .NET
/// <see cref="IndexOutOfRangeException"/> to the script, which is both wrong and a leak of
/// the host. Every builtin goes through here so a missing argument becomes a
/// <c>TypeError</c> worded as CPython words it — the fixtures compare the message.
/// </remarks>
public static class Arity
{
    /// <summary>Requires exactly <paramref name="expected"/> arguments for a plain function.</summary>
    public static void Exact(string function, PyObject[] arguments, int expected)
    {
        if (arguments.Length == expected)
        {
            return;
        }

        throw new PyRaise(PyErrors.TypeError(expected == 1
            ? $"{function}() takes exactly one argument ({arguments.Length} given)"
            : $"{function}() takes exactly {expected} arguments ({arguments.Length} given)"));
    }

    /// <summary>Requires between <paramref name="minimum"/> and <paramref name="maximum"/> arguments.</summary>
    public static void Between(string function, PyObject[] arguments, int minimum, int maximum)
    {
        if (arguments.Length >= minimum && arguments.Length <= maximum)
        {
            return;
        }

        if (arguments.Length < minimum)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{function} expected at least {minimum} argument{(minimum == 1 ? string.Empty : "s")}, got {arguments.Length}"));
        }

        throw new PyRaise(PyErrors.TypeError(
            $"{function} expected at most {maximum} argument{(maximum == 1 ? string.Empty : "s")}, got {arguments.Length}"));
    }

    /// <summary>
    /// Requires exactly <paramref name="expected"/> arguments, worded the other way round.
    /// </summary>
    /// <remarks>
    /// CPython is not consistent here: <c>len</c> reports "takes exactly one argument (2
    /// given)" while <c>hasattr</c> reports "expected 2 arguments, got 2". Both spellings
    /// exist so each builtin can produce the message it really produces.
    /// </remarks>
    public static void ExactCount(string function, PyObject[] arguments, int expected)
    {
        if (arguments.Length == expected)
        {
            return;
        }

        throw new PyRaise(PyErrors.TypeError(
            $"{function} expected {expected} argument{(expected == 1 ? string.Empty : "s")}, got {arguments.Length}"));
    }

    /// <summary>Reads an attribute-name argument, which must be a string.</summary>
    public static string AttributeName(PyObject value) =>
        value is PyStr text
            ? text.Value
            : throw new PyRaise(PyErrors.TypeError($"attribute name must be string, not '{value.TypeName}'"));

    /// <summary>Requires at least <paramref name="minimum"/> arguments.</summary>
    public static void AtLeast(string function, PyObject[] arguments, int minimum)
    {
        if (arguments.Length >= minimum)
        {
            return;
        }

        throw new PyRaise(PyErrors.TypeError(
            $"{function} expected at least {minimum} argument{(minimum == 1 ? string.Empty : "s")}, got {arguments.Length}"));
    }

    /// <summary>Requires exactly <paramref name="expected"/> arguments for a method of a built-in type.</summary>
    public static void Method(string typeName, string method, PyObject[] arguments, int expected)
    {
        if (arguments.Length == expected)
        {
            return;
        }

        throw new PyRaise(PyErrors.TypeError(expected == 1
            ? $"{typeName}.{method}() takes exactly one argument ({arguments.Length} given)"
            : $"{typeName}.{method}() takes exactly {expected} arguments ({arguments.Length} given)"));
    }

    /// <summary>Reads an argument, raising when it is absent.</summary>
    public static PyObject At(string function, PyObject[] arguments, int index)
    {
        if (index < arguments.Length)
        {
            return arguments[index];
        }

        throw new PyRaise(PyErrors.TypeError(
            $"{function} expected at least {index + 1} argument{(index == 0 ? string.Empty : "s")}, got {arguments.Length}"));
    }
}
