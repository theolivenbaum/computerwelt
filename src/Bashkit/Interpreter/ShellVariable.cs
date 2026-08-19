using System.Globalization;

namespace Bashkit.Interpreter;

/// <summary>Attributes a variable can carry, as <c>declare</c> sets them.</summary>
[Flags]
public enum VariableAttributes
{
    /// <summary>No attributes.</summary>
    None = 0,

    /// <summary><c>export</c> — placed into the environment of commands.</summary>
    Exported = 1 << 0,

    /// <summary><c>declare -r</c> — assignment is an error.</summary>
    ReadOnly = 1 << 1,

    /// <summary><c>declare -i</c> — assigned values are evaluated arithmetically.</summary>
    Integer = 1 << 2,

    /// <summary><c>declare -a</c> — indexed array.</summary>
    IndexedArray = 1 << 3,

    /// <summary><c>declare -A</c> — associative array.</summary>
    AssociativeArray = 1 << 4,

    /// <summary><c>declare -n</c> — the value names another variable.</summary>
    NameRef = 1 << 5,

    /// <summary><c>declare -u</c> — assigned values are upper-cased.</summary>
    UpperCase = 1 << 6,

    /// <summary><c>declare -l</c> — assigned values are lower-cased.</summary>
    LowerCase = 1 << 7,
}

/// <summary>
/// One shell variable.
/// </summary>
/// <remarks>
/// Scalars and arrays share a representation because bash does too: <c>x=a</c> followed by
/// <c>x[1]=b</c> silently promotes <c>x</c> to an array whose element 0 is <c>a</c>, and
/// <c>$x</c> then means <c>${x[0]}</c>. Modelling them as separate types would make that
/// promotion impossible to express.
/// </remarks>
public sealed class ShellVariable
{
    private readonly SortedDictionary<long, string> _indexed = [];
    private readonly Dictionary<string, string> _associative = new(StringComparer.Ordinal);

    /// <summary>Creates an unset variable with the given attributes.</summary>
    public ShellVariable(VariableAttributes attributes = VariableAttributes.None) =>
        Attributes = attributes;

    /// <summary>Creates a scalar variable.</summary>
    public static ShellVariable Scalar(string value, VariableAttributes attributes = VariableAttributes.None)
    {
        var variable = new ShellVariable(attributes);
        variable.SetScalar(value);
        return variable;
    }

    /// <summary>The variable's attributes.</summary>
    public VariableAttributes Attributes { get; set; }

    /// <summary>True when the variable holds an indexed or associative array.</summary>
    public bool IsArray => Attributes.HasFlag(VariableAttributes.IndexedArray)
        || Attributes.HasFlag(VariableAttributes.AssociativeArray);

    /// <summary>True when the variable is associative.</summary>
    public bool IsAssociative => Attributes.HasFlag(VariableAttributes.AssociativeArray);

    /// <summary>True when the variable is exported to command environments.</summary>
    public bool IsExported => Attributes.HasFlag(VariableAttributes.Exported);

    /// <summary>True when assignment to this variable is an error.</summary>
    public bool IsReadOnly => Attributes.HasFlag(VariableAttributes.ReadOnly);

    /// <summary>True when the variable has never been given a value.</summary>
    public bool IsUnset { get; private set; } = true;

    /// <summary>
    /// The scalar view. For an array this is element 0 (indexed) or the empty string
    /// (associative), which is what <c>$arr</c> expands to.
    /// </summary>
    public string Value
    {
        get
        {
            if (IsAssociative)
            {
                return _associative.TryGetValue("0", out var assoc) ? assoc : string.Empty;
            }

            return _indexed.TryGetValue(0, out var value) ? value : string.Empty;
        }
    }

    /// <summary>Replaces the variable's value with a scalar.</summary>
    public void SetScalar(string value)
    {
        if (IsAssociative)
        {
            _associative["0"] = Transform(value);
        }
        else
        {
            _indexed.Clear();
            _indexed[0] = Transform(value);
        }

        IsUnset = false;
    }

    /// <summary>Appends to the variable's scalar value.</summary>
    public void AppendScalar(string value) => SetScalar(Value + value);

    /// <summary>Sets one element of an indexed array.</summary>
    public void SetIndexed(long index, string value)
    {
        Attributes |= VariableAttributes.IndexedArray;
        _indexed[index] = Transform(value);
        IsUnset = false;
    }

    /// <summary>Sets one element of an associative array.</summary>
    public void SetAssociative(string key, string value)
    {
        Attributes |= VariableAttributes.AssociativeArray;
        _associative[key] = Transform(value);
        IsUnset = false;
    }

    /// <summary>Replaces the whole variable with an indexed array built from <paramref name="values"/>.</summary>
    public void SetArray(IEnumerable<string> values)
    {
        Attributes |= VariableAttributes.IndexedArray;
        _indexed.Clear();
        var i = 0L;
        foreach (var value in values)
        {
            _indexed[i++] = Transform(value);
        }

        IsUnset = false;
    }

    /// <summary>Appends elements to the end of an indexed array.</summary>
    public void AppendArray(IEnumerable<string> values)
    {
        Attributes |= VariableAttributes.IndexedArray;
        var next = _indexed.Count == 0 ? 0 : _indexed.Keys.Max() + 1;
        foreach (var value in values)
        {
            _indexed[next++] = Transform(value);
        }

        IsUnset = false;
    }

    /// <summary>Reads one element, or <see langword="null"/> when absent.</summary>
    public string? GetElement(string subscript)
    {
        if (IsAssociative)
        {
            return _associative.TryGetValue(subscript, out var value) ? value : null;
        }

        return long.TryParse(subscript, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && _indexed.TryGetValue(NormalizeIndex(index), out var element)
            ? element
            : null;
    }

    /// <summary>Removes one element.</summary>
    public void UnsetElement(string subscript)
    {
        if (IsAssociative)
        {
            _associative.Remove(subscript);
            return;
        }

        if (long.TryParse(subscript, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            _indexed.Remove(NormalizeIndex(index));
        }
    }

    /// <summary>Every element value, in subscript order.</summary>
    public IReadOnlyList<string> Elements =>
        IsAssociative ? [.. _associative.Values] : [.. _indexed.Values];

    /// <summary>Every subscript, in order.</summary>
    public IReadOnlyList<string> Keys => IsAssociative
        ? [.. _associative.Keys]
        : [.. _indexed.Keys.Select(static k => k.ToString(CultureInfo.InvariantCulture))];

    /// <summary>Number of elements. A scalar counts as one.</summary>
    public int Count => IsAssociative ? _associative.Count : _indexed.Count;

    // A negative subscript counts back from the end, so `${a[-1]}` is the last element.
    private long NormalizeIndex(long index)
    {
        if (index >= 0)
        {
            return index;
        }

        var highest = _indexed.Count == 0 ? -1 : _indexed.Keys.Max();
        return highest + 1 + index;
    }

    private string Transform(string value)
    {
        if (Attributes.HasFlag(VariableAttributes.UpperCase))
        {
            return value.ToUpperInvariant();
        }

        if (Attributes.HasFlag(VariableAttributes.LowerCase))
        {
            return value.ToLowerInvariant();
        }

        return value;
    }
}
