using System.Globalization;
using System.Numerics;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Playwright.Interop;

/// <summary>
/// Reads one call's arguments the way a Python function signature does.
/// </summary>
/// <remarks>
/// <para>
/// The upstream API is almost entirely keyword arguments, and the reason to model that
/// faithfully is not tidiness: a keyword this port does not implement must be an error the
/// script sees, because silently dropping <c>wait_until='networkidle'</c> would leave a
/// program believing it waited. So every method names the arguments it understands and
/// <see cref="Done"/> refuses whatever is left over, exactly as CPython does.
/// </para>
/// <para>
/// Arguments that are positional-or-keyword upstream are readable either way here, by index
/// and by name together.
/// </para>
/// </remarks>
internal sealed class Arguments
{
    private readonly PyObject[] _positional;
    private readonly PyDict? _keywords;
    private readonly HashSet<string> _taken = new(StringComparer.Ordinal);
    private int _consumed;

    public Arguments(string name, PyObject[] positional, PyDict? keywords)
    {
        Name = name;
        _positional = positional;
        _keywords = keywords;
    }

    /// <summary>The method being called, for error messages.</summary>
    public string Name { get; }

    /// <summary>How many positional arguments were supplied.</summary>
    public int Count => _positional.Length;

    /// <summary>The value of a positional-or-keyword argument, or null when it was not given.</summary>
    public PyObject? Value(int index, string name)
    {
        if (_keywords is not null && _keywords.TryGetValue(new PyStr(name), out var keyword))
        {
            if (index < _positional.Length)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{Name}() got multiple values for argument '{name}'"));
            }

            _taken.Add(name);
            return keyword is PyNone ? null : keyword;
        }

        if (index < _positional.Length)
        {
            _consumed = Math.Max(_consumed, index + 1);
            return _positional[index] is PyNone ? null : _positional[index];
        }

        return null;
    }

    /// <summary>The value of a keyword-only argument, or null when it was not given.</summary>
    public PyObject? Keyword(string name)
    {
        if (_keywords is null || !_keywords.TryGetValue(new PyStr(name), out var value))
        {
            return null;
        }

        _taken.Add(name);
        return value is PyNone ? null : value;
    }

    /// <summary>A required positional-or-keyword string.</summary>
    public string String(int index, string name) =>
        Text(Value(index, name) ?? throw new PyRaise(PyErrors.TypeError(
            $"{Name}() missing required argument: '{name}'")), name);

    /// <summary>An optional positional-or-keyword string.</summary>
    public string? OptionalString(int index, string name) =>
        Value(index, name) is { } value ? Text(value, name) : null;

    /// <summary>An optional keyword-only string.</summary>
    public string? String(string name) => Keyword(name) is { } value ? Text(value, name) : null;

    /// <summary>An optional keyword-only boolean.</summary>
    public bool? Bool(string name) => Keyword(name) switch
    {
        null => null,
        PyBool flag => flag.Value,
        PyInt number => !number.Value.IsZero,
        var other => throw new PyRaise(PyErrors.TypeError(
            $"{Name}(): '{name}' must be a bool, not '{other.TypeName}'")),
    };

    /// <summary>A required positional-or-keyword boolean.</summary>
    public bool RequiredBool(int index, string name) => Value(index, name) switch
    {
        PyBool flag => flag.Value,
        PyInt number => !number.Value.IsZero,
        null => throw new PyRaise(PyErrors.TypeError($"{Name}() missing required argument: '{name}'")),
        var other => throw new PyRaise(PyErrors.TypeError(
            $"{Name}(): '{name}' must be a bool, not '{other.TypeName}'")),
    };

    /// <summary>An optional keyword-only number, in whatever unit upstream uses.</summary>
    public double? Number(string name) => Keyword(name) switch
    {
        null => null,
        PyInt integer => (double)integer.Value,
        PyFloat number => number.Value,
        var other => throw new PyRaise(PyErrors.TypeError(
            $"{Name}(): '{name}' must be a number, not '{other.TypeName}'")),
    };

    /// <summary>An optional keyword-only integer.</summary>
    public int? Integer(string name) => Keyword(name) switch
    {
        null => null,
        PyInt integer => Index(integer.Value, name),
        var other => throw new PyRaise(PyErrors.TypeError(
            $"{Name}(): '{name}' must be an int, not '{other.TypeName}'")),
    };

    /// <summary>A required positional-or-keyword integer.</summary>
    public int RequiredInteger(int index, string name) => Value(index, name) switch
    {
        PyInt integer => Index(integer.Value, name),
        null => throw new PyRaise(PyErrors.TypeError($"{Name}() missing required argument: '{name}'")),
        var other => throw new PyRaise(PyErrors.TypeError(
            $"{Name}(): '{name}' must be an int, not '{other.TypeName}'")),
    };

    /// <summary>An optional keyword-only <c>timeout=</c>, in milliseconds.</summary>
    public double? Timeout() => Number("timeout");

    /// <summary>
    /// An optional keyword-only argument that is a string or a sequence of them.
    /// </summary>
    /// <remarks>
    /// Upstream spells several of these as <c>Union[str, Sequence[str]]</c> and a script
    /// written against it passes either, so both are read here rather than only the plural.
    /// </remarks>
    public IReadOnlyList<string>? Strings(string name) => Sequence(Keyword(name), name);

    /// <summary>The same, for a positional-or-keyword argument.</summary>
    public IReadOnlyList<string>? Strings(int index, string name) => Sequence(Value(index, name), name);

    /// <summary>An optional positional-or-keyword <c>str | Pattern</c>.</summary>
    public Matcher? Matcher(int index, string name) =>
        Interop.Matcher.From(Value(index, name), Name, name);

    /// <summary>An optional keyword-only <c>str | Pattern</c>.</summary>
    public Matcher? Matcher(string name) => Interop.Matcher.From(Keyword(name), Name, name);

    /// <summary>A required positional-or-keyword <c>str | Pattern | Sequence</c> of either.</summary>
    public MatcherList Matchers(int index, string name) =>
        Interop.Matcher.Many(Value(index, name), Name, name);

    /// <summary>An optional keyword-only mapping of strings to strings, such as HTTP headers.</summary>
    public IReadOnlyDictionary<string, string>? Headers(string name)
    {
        if (Keyword(name) is not { } value)
        {
            return null;
        }

        if (value is not PyDict dictionary)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{Name}(): '{name}' must be a dict, not '{value.TypeName}'"));
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, item) in dictionary.Entries)
        {
            result[Text(key, name)] = Text(item, name);
        }

        return result;
    }

    /// <summary>An optional keyword-only <c>{'x': …, 'y': …}</c> pair, as several options take.</summary>
    public (float X, float Y)? Point(string name)
    {
        if (Keyword(name) is not { } value)
        {
            return null;
        }

        if (value is not PyDict dictionary)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{Name}(): '{name}' must be a dict with 'x' and 'y', not '{value.TypeName}'"));
        }

        return ((float)Coordinate(dictionary, "x", name), (float)Coordinate(dictionary, "y", name));
    }

    /// <summary>
    /// Refuses anything the method did not name.
    /// </summary>
    /// <remarks>
    /// The honest half of the port: a keyword that was not read is one this implementation
    /// does not honour, and a script is told so rather than left to infer it from behaviour
    /// that quietly differs from upstream's.
    /// </remarks>
    /// <summary>
    /// Keywords upstream still accepts and the driver has turned into no-ops.
    /// </summary>
    /// <remarks>
    /// Passing one of these to the .NET driver is now an error, and refusing it here would
    /// break scripts written against a version of the Python API where it still means
    /// something. Accepting and ignoring it is what upstream itself does.
    /// </remarks>
    private static readonly HashSet<string> Deprecated = new(StringComparer.Ordinal) { "no_wait_after" };

    public void Done(int positional = int.MaxValue)
    {
        var allowed = positional == int.MaxValue ? _consumed : positional;

        if (_positional.Length > allowed)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{Name}() takes {allowed} positional argument(s) but {_positional.Length} were given"));
        }

        if (_keywords is null)
        {
            return;
        }

        foreach (var (key, _) in _keywords.Entries)
        {
            var name = key is PyStr text ? text.Value : key.Display();

            if (!_taken.Contains(name) && !Deprecated.Contains(name))
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{Name}() got an unexpected keyword argument '{name}'"));
            }
        }
    }

    private string Text(PyObject value, string name) => value switch
    {
        PyStr text => text.Value,
        _ => throw new PyRaise(PyErrors.TypeError(
            $"{Name}(): '{name}' must be a str, not '{value.TypeName}'")),
    };

    private IReadOnlyList<string>? Sequence(PyObject? value, string name)
    {
        switch (value)
        {
            case null:
                return null;

            case PyStr text:
                return [text.Value];

            default:
                var items = value.Iterate() ?? throw new PyRaise(PyErrors.TypeError(
                    $"{Name}(): '{name}' must be a str or a sequence of str, not '{value.TypeName}'"));

                return [.. items.Select(item => Text(item, name))];
        }
    }

    private double Coordinate(PyDict dictionary, string key, string name)
    {
        if (!dictionary.TryGetValue(new PyStr(key), out var value))
        {
            throw new PyRaise(PyErrors.TypeError($"{Name}(): '{name}' is missing '{key}'"));
        }

        return value switch
        {
            PyInt integer => (double)integer.Value,
            PyFloat number => number.Value,
            _ => throw new PyRaise(PyErrors.TypeError(
                $"{Name}(): '{name}[{key}]' must be a number, not '{value.TypeName}'")),
        };
    }

    private int Index(BigInteger value, string name)
    {
        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new PyRaise(PyErrors.ValueError(
                $"{Name}(): '{name}' is out of range: {value.ToString(CultureInfo.InvariantCulture)}"));
        }

        return (int)value;
    }
}
