using System.Globalization;
using Monty.Parsing;

namespace Monty.Runtime;

/// <summary>
/// The class object a <c>collections.namedtuple</c> call returns, and the type of a
/// structseq such as <c>sys.version_info</c>.
/// </summary>
/// <remarks>
/// A structseq shares this type but hides the <c>_</c>-prefixed helpers, exactly as
/// CPython does: <c>sys.version_info</c> has named fields but is not a
/// <c>collections.namedtuple</c>, so <c>_asdict</c> and friends must not appear on it.
/// </remarks>
public sealed class PyNamedTupleType : PyCallable
{
    /// <summary>Creates a named-tuple class.</summary>
    public PyNamedTupleType(
        string name,
        IReadOnlyList<string> fields,
        IReadOnlyList<PyObject>? defaults = null,
        PyObject? module = null,
        bool structSeq = false)
    {
        Name = name;
        Fields = fields;
        Defaults = defaults ?? [];
        Module = module ?? new PyStr("__main__");
        IsStructSeq = structSeq;
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>The field names, in order.</summary>
    public IReadOnlyList<string> Fields { get; }

    /// <summary>Default values for the rightmost fields.</summary>
    public IReadOnlyList<PyObject> Defaults { get; }

    /// <summary>The value of <c>__module__</c>, stored unvalidated as CPython does.</summary>
    public PyObject Module { get; }

    /// <summary>Whether this models a CPython structseq rather than a namedtuple class.</summary>
    public bool IsStructSeq { get; }

    /// <inheritdoc />
    public override string TypeName => "type";

    /// <inheritdoc />
    public override string Repr() => $"<class '{Name}'>";

    /// <summary>The synthesised class docstring, <c>Name(a, b)</c>.</summary>
    public string Doc =>
        // A one-field class keeps the tuple comma, because CPython builds this from the
        // repr of the field tuple.
        Name + "(" + string.Join(", ", Fields) + (Fields.Count == 1 ? "," : string.Empty) + ")";

    /// <summary>The defaults as a <c>{field: value}</c> mapping.</summary>
    public PyDict FieldDefaults
    {
        get
        {
            var defaults = new PyDict();

            for (var i = 0; i < Defaults.Count; i++)
            {
                defaults.Set(new PyStr(Fields[Fields.Count - Defaults.Count + i]), Defaults[i]);
            }

            return defaults;
        }
    }

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "__name__" or "__qualname__" => new PyStr(Name),
        "__doc__" => new PyStr(Doc),
        "__module__" => Module,
        "_fields" when !IsStructSeq => new PyTuple([.. Fields.Select(static f => new PyStr(f))]),
        "_field_defaults" when !IsStructSeq => FieldDefaults,
        "_make" when !IsStructSeq => new PyBuiltinFunction("_make", arguments => Make(arguments)),
        _ => null,
    };

    /// <summary>Builds an instance from an iterable of values.</summary>
    public PyObject Make(PyObject[] arguments)
    {
        var values = VirtualMachine.RequireIterable(arguments[0]).ToList();

        return values.Count == Fields.Count
            ? new PyNamedTuple(this, values)
            : throw new PyRaise(PyErrors.TypeError(
                $"Expected {Fields.Count} arguments, got {values.Count}"));
    }

    /// <summary>Builds an instance from positional and keyword arguments.</summary>
    /// <remarks>
    /// The errors name the generated <c>__new__</c>, and count the implicit <c>cls</c>
    /// among the positional arguments, because that is what CPython 3.14 reports.
    /// </remarks>
    public PyObject Instantiate(PyObject[] arguments, PyDict? keywords)
    {
        if (arguments.Length > Fields.Count)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{Name}.__new__() takes {Fields.Count + 1} positional arguments but {arguments.Length + 1} were given"));
        }

        var values = new List<PyObject>(Fields.Count);

        for (var i = 0; i < Fields.Count; i++)
        {
            var key = new PyStr(Fields[i]);
            var named = keywords is not null && keywords.TryGetValue(key, out var keyword) ? keyword : null;

            if (i < arguments.Length)
            {
                if (named is not null)
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"{Name}.__new__() got multiple values for argument '{Fields[i]}'"));
                }

                values.Add(arguments[i]);
                continue;
            }

            if (named is not null)
            {
                values.Add(named);
                continue;
            }

            var defaulted = i - (Fields.Count - Defaults.Count);

            if (defaulted >= 0)
            {
                values.Add(Defaults[defaulted]);
                continue;
            }

            var missing = Fields.Skip(i).Where(f =>
                keywords is null || !keywords.TryGetValue(new PyStr(f), out _)).ToList();

            throw new PyRaise(PyErrors.TypeError(
                $"{Name}.__new__() missing {missing.Count} required positional argument"
                + (missing.Count == 1 ? string.Empty : "s") + ": "
                + Join(missing)));
        }

        foreach (var (key, _) in keywords?.Entries ?? [])
        {
            if (!Fields.Contains(key.Display()))
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{Name}.__new__() got an unexpected keyword argument '{key.Display()}'"));
            }
        }

        return new PyNamedTuple(this, values);
    }

    /// <summary>Formats a list of names the way CPython's arity errors do.</summary>
    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        1 => $"'{names[0]}'",
        2 => $"'{names[0]}' and '{names[1]}'",
        _ => string.Join(", ", names.Take(names.Count - 1).Select(n => $"'{n}'"))
            + $", and '{names[^1]}'",
    };

    /// <summary>
    /// Splits a <c>namedtuple</c> field specification: a string of space- or
    /// comma-separated names, or any iterable of names.
    /// </summary>
    public static List<string> ParseFields(PyObject specification) =>
        specification is PyStr text
            ? [.. text.Value.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)]
            : [.. VirtualMachine.RequireIterable(specification).Select(static f => f.Display())];

    /// <summary>
    /// Validates a type name and its fields, renaming the invalid ones when
    /// <paramref name="rename"/> asks for it.
    /// </summary>
    public static void Validate(string typeName, List<string> fields, bool rename)
    {
        Check(typeName);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < fields.Count; i++)
        {
            if (rename)
            {
                // A renamed field is named for its position, so the replacement can never
                // collide with an earlier one.
                if (!Identifiers.IsIdentifier(fields[i])
                    || Keywords.IsReserved(fields[i])
                    || fields[i].StartsWith('_')
                    || !seen.Add(fields[i]))
                {
                    fields[i] = "_" + i.ToString(CultureInfo.InvariantCulture);
                }

                continue;
            }

            Check(fields[i]);

            if (fields[i].StartsWith('_'))
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"Field names cannot start with an underscore: {PyStr.Quote(fields[i])}"));
            }

            if (!seen.Add(fields[i]))
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"Encountered duplicate field name: {PyStr.Quote(fields[i])}"));
            }
        }

        static void Check(string name)
        {
            if (Keywords.IsReserved(name))
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"Type names and field names cannot be a keyword: {PyStr.Quote(name)}"));
            }

            if (!Identifiers.IsIdentifier(name))
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"Type names and field names must be valid identifiers: {PyStr.Quote(name)}"));
            }
        }
    }
}

/// <summary>
/// An instance of a named tuple: a real tuple whose elements also answer to names.
/// </summary>
/// <remarks>
/// Subclassing <see cref="PyTuple"/> rather than wrapping one is what makes slicing,
/// concatenation, ordering, hashing and <c>isinstance(x, tuple)</c> behave — all of which
/// CPython inherits the same way.
/// </remarks>
public sealed class PyNamedTuple : PyTuple
{
    /// <summary>Creates an instance of <paramref name="type"/>.</summary>
    public PyNamedTuple(PyNamedTupleType type, IReadOnlyList<PyObject> values)
        : base(values) => Type = type;

    /// <summary>The class this instance belongs to.</summary>
    public PyNamedTupleType Type { get; }

    /// <inheritdoc />
    public override string TypeName => Type.Name;

    /// <inheritdoc />
    public override string Repr() =>
        Type.Name + "("
        + string.Join(", ", Type.Fields.Zip(Items, static (field, value) => $"{field}={value.Repr()}"))
        + ")";

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name)
    {
        var index = -1;

        for (var i = 0; i < Type.Fields.Count; i++)
        {
            if (string.Equals(Type.Fields[i], name, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index >= 0)
        {
            return Items[index];
        }

        // A structseq's `__new__` takes one sequence, so its `__getnewargs__` wraps the
        // values a level deeper than a namedtuple's, which returns them flat.
        if (name == "__getnewargs__")
        {
            return new PyBuiltinFunction("__getnewargs__", _ =>
                Type.IsStructSeq ? new PyTuple([new PyTuple(Items)]) : new PyTuple(Items));
        }

        return name switch
        {
            "_asdict" when !Type.IsStructSeq => new PyBuiltinFunction("_asdict", _ => AsDict()),
            "_replace" when !Type.IsStructSeq =>
                new PyBuiltinFunction("_replace", (arguments, keywords) => Replace(arguments, keywords)),
            _ => Type.GetAttribute(name) is { } inherited && name is not "__name__" and not "__qualname__"
                ? inherited
                : base.GetAttribute(name),
        };
    }

    private PyDict AsDict()
    {
        var entries = new PyDict();

        for (var i = 0; i < Type.Fields.Count; i++)
        {
            entries.Set(new PyStr(Type.Fields[i]), Items[i]);
        }

        return entries;
    }

    private PyObject Replace(PyObject[] arguments, PyDict? keywords)
    {
        if (arguments.Length > 0)
        {
            throw new PyRaise(PyErrors.TypeError("_replace() takes 1 positional argument"));
        }

        var values = Items.ToList();
        var unexpected = new List<string>();

        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            var name = key.Display();
            var index = -1;

            for (var i = 0; i < Type.Fields.Count; i++)
            {
                if (string.Equals(Type.Fields[i], name, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                unexpected.Add(name);
                continue;
            }

            values[index] = value;
        }

        return unexpected.Count > 0
            ? throw new PyRaise(PyErrors.TypeError(
                "Got unexpected field names: [" + string.Join(", ", unexpected.Select(PyStr.Quote)) + "]"))
            : new PyNamedTuple(Type, values);
    }
}

/// <summary>Python's identifier rules, shared by <c>str.isidentifier</c> and namedtuple.</summary>
public static class Identifiers
{
    /// <summary>
    /// True when <paramref name="text"/> is a valid Python identifier.
    /// </summary>
    /// <remarks>
    /// Python uses the Unicode XID rules, not ASCII: <c>café</c> is an identifier, and so
    /// is a base letter followed by a combining mark.
    /// </remarks>
    public static bool IsIdentifier(string text)
    {
        if (text.Length == 0 || !IsStart(char.GetUnicodeCategory(text, 0)) && text[0] != '_')
        {
            return false;
        }

        for (var i = 1; i < text.Length; i++)
        {
            if (char.IsLowSurrogate(text[i]))
            {
                continue;
            }

            var category = char.GetUnicodeCategory(text, i);

            if (!IsStart(category) && text[i] != '_' && category is not (
                System.Globalization.UnicodeCategory.NonSpacingMark
                or System.Globalization.UnicodeCategory.SpacingCombiningMark
                or System.Globalization.UnicodeCategory.DecimalDigitNumber
                or System.Globalization.UnicodeCategory.ConnectorPunctuation))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsStart(System.Globalization.UnicodeCategory category) => category is
        System.Globalization.UnicodeCategory.UppercaseLetter
        or System.Globalization.UnicodeCategory.LowercaseLetter
        or System.Globalization.UnicodeCategory.TitlecaseLetter
        or System.Globalization.UnicodeCategory.ModifierLetter
        or System.Globalization.UnicodeCategory.OtherLetter
        or System.Globalization.UnicodeCategory.LetterNumber;
}
