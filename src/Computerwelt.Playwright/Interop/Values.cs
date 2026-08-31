using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Playwright.Interop;

/// <summary>
/// Carries values across the boundary between the sandbox and the page.
/// </summary>
/// <remarks>
/// The wire between them is JSON, which is narrower than either side: a Python object with
/// no JSON shape cannot be sent, and a JavaScript value with none cannot be returned. Both
/// directions say so rather than approximating — a set that arrived as a list, or a
/// <c>function</c> that arrived as <c>None</c>, is a bug the script would find much later.
/// </remarks>
internal static class Values
{
    /// <summary>Turns a Python value into the argument <c>evaluate</c> sends to the page.</summary>
    public static object? ToScript(PyObject? value, string where) => value switch
    {
        null or PyNone => null,
        PyBool flag => flag.Value,
        PyInt integer => Number(integer.Value),
        PyFloat number => number.Value,
        PyStr text => text.Value,
        PyList list => list.Items.Select(item => ToScript(item, where)).ToArray(),
        PyTuple tuple => tuple.Items.Select(item => ToScript(item, where)).ToArray(),
        PyDict dictionary => Mapping(dictionary, where),

        _ => throw new PyRaise(PyErrors.TypeError(
            $"{where}: cannot send a '{value.TypeName}' to the page; "
            + "the argument must be None, a bool, a number, a str, a list, a tuple or a dict")),
    };

    /// <summary>Turns what the page returned into a Python value.</summary>
    public static PyObject FromScript(JsonElement? element) =>
        element is { } value ? FromJson(value) : PyNone.Instance;

    /// <summary>Turns a JSON value into the Python one that matches it.</summary>
    public static PyObject FromJson(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return PyNone.Instance;

            case JsonValueKind.True:
                return PyBool.True;

            case JsonValueKind.False:
                return PyBool.False;

            case JsonValueKind.String:
                return new PyStr(element.GetString() ?? string.Empty);

            case JsonValueKind.Number:
                // JavaScript has one number type, so `2` comes back as `2` and not `2.0`:
                // a whole number becomes an int, which is what a script comparing it to a
                // count expects.
                return element.TryGetInt64(out var integer)
                    ? PyInt.From(integer)
                    : new PyFloat(element.GetDouble());

            case JsonValueKind.Array:
            {
                var items = new List<PyObject>(element.GetArrayLength());

                foreach (var item in element.EnumerateArray())
                {
                    items.Add(FromJson(item));
                }

                return new PyList(items);
            }

            default:
            {
                var dictionary = new PyDict();

                foreach (var property in element.EnumerateObject())
                {
                    dictionary.Set(new PyStr(property.Name), FromJson(property.Value));
                }

                return dictionary;
            }
        }
    }

    /// <summary>Turns a mapping the driver produced — headers, cookies — into a <c>dict</c>.</summary>
    public static PyDict FromMapping(IEnumerable<KeyValuePair<string, string>> entries)
    {
        var dictionary = new PyDict();

        foreach (var (key, value) in entries)
        {
            dictionary.Set(new PyStr(key), new PyStr(value));
        }

        return dictionary;
    }

    /// <summary>Turns a sequence of strings into a <c>list</c>.</summary>
    public static PyList FromStrings(IEnumerable<string?> values) =>
        new([.. values.Select(static value => value is null ? (PyObject)PyNone.Instance : new PyStr(value))]);

    /// <summary>A <c>str</c>, or <c>None</c> when the driver had nothing to report.</summary>
    public static PyObject FromOptional(string? value) => value is null ? PyNone.Instance : new PyStr(value);

    /// <summary>Parses JSON the driver returned as text — a response body, a storage state.</summary>
    public static PyObject FromJsonText(string text, string where)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return FromJson(document.RootElement.Clone());
        }
        catch (JsonException error)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.JsonDecodeError, $"{where}: {error.Message}"));
        }
    }

    private static object Number(BigInteger value)
    {
        // JavaScript numbers are doubles, so an integer past 2^53 cannot survive the trip
        // intact. Saying so beats sending a number the page would silently round.
        if (value < long.MinValue || value > long.MaxValue)
        {
            throw new PyRaise(PyErrors.ValueError(
                $"{value.ToString(CultureInfo.InvariantCulture)} is too large to send to the page"));
        }

        return (long)value;
    }

    private static Dictionary<string, object?> Mapping(PyDict dictionary, string where)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, value) in dictionary.Entries)
        {
            if (key is not PyStr text)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{where}: a dict sent to the page must have str keys, not '{key.TypeName}'"));
            }

            result[text.Value] = ToScript(value, where);
        }

        return result;
    }
}
