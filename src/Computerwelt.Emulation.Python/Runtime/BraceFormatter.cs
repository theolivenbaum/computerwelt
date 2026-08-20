using System.Globalization;
using System.Text;

namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>Implements <c>str.format</c>.</summary>
/// <remarks>
/// Shares the format spec with f-strings via <see cref="StringFormatter.Format"/>; what is
/// specific here is field <i>selection</i> — positional, named, indexed and attribute
/// access inside the braces.
/// </remarks>
public static class BraceFormatter
{
    /// <summary>Formats <paramref name="format"/> with the given arguments.</summary>
    public static string Format(string format, PyObject[] arguments, PyDict? keywords)
    {
        var builder = new StringBuilder(format.Length);
        var automatic = 0;

        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];

            if (c == '{' && i + 1 < format.Length && format[i + 1] == '{')
            {
                builder.Append('{');
                i++;
                continue;
            }

            if (c == '}' && i + 1 < format.Length && format[i + 1] == '}')
            {
                builder.Append('}');
                i++;
                continue;
            }

            if (c != '{')
            {
                builder.Append(c);
                continue;
            }

            var end = FindClose(format, i);

            if (end < 0)
            {
                throw new PyRaise(PyErrors.ValueError("Single '{' encountered in format string"));
            }

            builder.Append(FormatField(format[(i + 1)..end], arguments, keywords, ref automatic));
            i = end;
        }

        return builder.ToString();
    }

    private static int FindClose(string format, int open)
    {
        var depth = 0;

        for (var i = open; i < format.Length; i++)
        {
            switch (format[i])
            {
                case '{':
                    depth++;
                    break;

                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    private static string FormatField(string field, PyObject[] arguments, PyDict? keywords, ref int automatic)
    {
        var (selector, conversion, spec) = Split(field);
        var value = Select(selector, arguments, keywords, ref automatic);

        var converted = conversion switch
        {
            'r' => new PyStr(value.Repr()),
            's' => new PyStr(value.Display()),
            'a' => new PyStr(value.Repr()),
            _ => value,
        };

        // A nested field in the spec is itself formatted first: `{:{width}}`.
        if (spec.Contains('{', StringComparison.Ordinal))
        {
            spec = Format(spec, arguments, keywords);
        }

        return StringFormatter.Format(converted, spec);
    }

    private static (string Selector, char Conversion, string Spec) Split(string field)
    {
        var depth = 0;

        for (var i = 0; i < field.Length; i++)
        {
            switch (field[i])
            {
                case '{' or '[':
                    depth++;
                    continue;

                case '}' or ']':
                    depth--;
                    continue;

                case '!' when depth == 0 && i + 1 < field.Length:
                {
                    var conversion = field[i + 1];
                    var rest = field[(i + 2)..];
                    return (field[..i], conversion, rest.StartsWith(':') ? rest[1..] : string.Empty);
                }

                case ':' when depth == 0:
                    return (field[..i], '\0', field[(i + 1)..]);
            }
        }

        return (field, '\0', string.Empty);
    }

    /// <summary>Resolves a field selector such as <c>0</c>, <c>name</c>, <c>0[key]</c> or <c>x.attr</c>.</summary>
    private static PyObject Select(string selector, PyObject[] arguments, PyDict? keywords, ref int automatic)
    {
        var head = selector;
        var accessors = string.Empty;

        var firstAccessor = selector.IndexOfAny(['.', '[']);

        if (firstAccessor >= 0)
        {
            head = selector[..firstAccessor];
            accessors = selector[firstAccessor..];
        }

        PyObject value;

        if (head.Length == 0)
        {
            // An empty selector consumes the next positional argument.
            if (automatic >= arguments.Length)
            {
                throw new PyRaise(PyErrors.IndexError("Replacement index out of range for positional args tuple"));
            }

            value = arguments[automatic++];
        }
        else if (int.TryParse(head, CultureInfo.InvariantCulture, out var index))
        {
            if (index >= arguments.Length)
            {
                throw new PyRaise(PyErrors.IndexError("Replacement index out of range for positional args tuple"));
            }

            value = arguments[index];
        }
        else
        {
            if (keywords is null || !keywords.TryGetValue(new PyStr(head), out value!))
            {
                throw new PyRaise(PyErrors.KeyError(new PyStr(head)));
            }
        }

        return ApplyAccessors(value, accessors);
    }

    private static PyObject ApplyAccessors(PyObject value, string accessors)
    {
        var i = 0;

        while (i < accessors.Length)
        {
            if (accessors[i] == '.')
            {
                var end = accessors.IndexOfAny(['.', '['], i + 1);
                end = end < 0 ? accessors.Length : end;
                var name = accessors[(i + 1)..end];

                value = value.GetAttribute(name)
                    ?? throw new PyRaise(PyErrors.AttributeError(value.TypeName, name));

                i = end;
                continue;
            }

            if (accessors[i] == '[')
            {
                var end = accessors.IndexOf(']', i);

                if (end < 0)
                {
                    throw new PyRaise(PyErrors.ValueError("Missing ']' in format string"));
                }

                var key = accessors[(i + 1)..end];

                // An all-digit key is an index; anything else is a mapping key.
                value = int.TryParse(key, CultureInfo.InvariantCulture, out var position)
                    ? value.GetItem(new PyInt(position))
                    : value.GetItem(new PyStr(key));

                i = end + 1;
                continue;
            }

            break;
        }

        return value;
    }
}
