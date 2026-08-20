using System.Text;
using Monty.Runtime;

namespace Monty.Modules;

/// <summary>The <c>unicodedata</c> module.</summary>
/// <remarks>
/// Backed by the tables in <see cref="UnicodeData"/> rather than by the host runtime, so
/// every answer comes from one Unicode release and none of them varies with the machine.
/// Normalization is the exception: it is the host's, because the algorithm is large and its
/// results for anything a script is likely to normalize have been stable for decades.
/// </remarks>
public static class UnicodedataModule
{
    /// <summary>Builds the module.</summary>
    /// <returns>The module.</returns>
    public static PyModuleObject Create()
    {
        var module = new PyModuleObject("unicodedata");

        module.Add("unidata_version", new PyStr(UnicodeData.Version));

        module.Add("category", new PyBuiltinFunction("category", static arguments =>
            new PyStr(UnicodeData.Category(Single(arguments, "category")))));

        module.Add("combining", new PyBuiltinFunction("combining", static arguments =>
            new PyInt(UnicodeData.Combining(Single(arguments, "combining")))));

        // `name` takes an optional default, and takes no keywords at all — the arity wording
        // is `PyArg_UnpackTuple`'s range form rather than the clinic's exact count.
        module.Add("name", new PyBuiltinFunction("name", static (arguments, keywords) =>
        {
            if (keywords is { Count: > 0 })
            {
                throw new PyRaise(PyErrors.TypeError("unicodedata.name() takes no keyword arguments"));
            }

            if (arguments.Length == 0)
            {
                throw new PyRaise(PyErrors.TypeError("name expected at least 1 argument, got 0"));
            }

            if (arguments.Length > 2)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"name expected at most 2 arguments, got {arguments.Length}"));
            }

            var found = UnicodeData.Name(Single(arguments, "name"));

            return found is not null ? new PyStr(found)
                : arguments.Length > 1 ? arguments[1]
                : throw new PyRaise(PyErrors.ValueError("no such name"));
        }));

        module.Add("lookup", new PyBuiltinFunction("lookup", static arguments =>
        {
            var name = arguments[0] switch
            {
                PyStr text => text.Value,
                PyBytes bytes => Encoding.UTF8.GetString(bytes.Value),
                var bad => throw new PyRaise(PyErrors.TypeError(
                    $"lookup() argument must be str, not {bad.TypeName}")),
            };

            return UnicodeData.Lookup(name) is { } codePoint
                ? new PyStr(char.ConvertFromUtf32(codePoint))
                : throw new PyRaise(new PyException(
                    PyExceptionType.KeyError,
                    $"undefined character name '{name}'",
                    [new PyStr($"undefined character name '{name}'")]));
        }));

        module.Add("normalize", new PyBuiltinFunction("normalize", static arguments =>
            new PyStr(Normalized("normalize", arguments))));

        module.Add("is_normalized", new PyBuiltinFunction("is_normalized", static arguments =>
        {
            var text = Text("is_normalized", arguments);
            return PyBool.Of(string.Equals(Normalized("is_normalized", arguments), text, StringComparison.Ordinal));
        }));

        return module;
    }

    /// <summary>Reads the one character a per-character function takes.</summary>
    private static int Single(PyObject[] arguments, string function)
    {
        if (arguments.Length == 0 || arguments[0] is not PyStr text)
        {
            var given = arguments.Length == 0 ? "nothing" : arguments[0].TypeName;

            throw new PyRaise(PyErrors.TypeError(
                $"{function}() argument must be a unicode character, not {given}"));
        }

        // "One character" means one code point, so a surrogate pair still counts as one.
        var length = text.Length() ?? text.Value.Length;

        return length == 1
            ? char.ConvertToUtf32(text.Value, 0)
            : throw new PyRaise(PyErrors.TypeError(
                $"{function}(): argument must be a unicode character, not a string of length {length}"));
    }

    /// <summary>Reads the string argument of a normalization function.</summary>
    private static string Text(string function, PyObject[] arguments) =>
        arguments.Length > 1 && arguments[1] is PyStr text
            ? text.Value
            : throw new PyRaise(PyErrors.TypeError(
                $"{function}() argument 2 must be str, not "
                + (arguments.Length > 1 ? arguments[1].TypeName : "nothing")));

    /// <summary>Normalizes, checking both argument types before the form's value.</summary>
    private static string Normalized(string function, PyObject[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] is not PyStr form)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{function}() argument 1 must be str, not "
                + (arguments.Length == 0 ? "nothing" : arguments[0].TypeName)));
        }

        var text = Text(function, arguments);

        return form.Value switch
        {
            "NFC" => text.Normalize(NormalizationForm.FormC),
            "NFD" => text.Normalize(NormalizationForm.FormD),
            "NFKC" => text.Normalize(NormalizationForm.FormKC),
            "NFKD" => text.Normalize(NormalizationForm.FormKD),
            _ => throw new PyRaise(PyErrors.ValueError("invalid normalization form")),
        };
    }
}
