using System.Globalization;

namespace Computerwelt.Emulation.Bash.Interpreter;

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
    // Storage is allocated only once the variable needs it. The overwhelmingly common
    // shape — a scalar, or a one-element array such as FUNCNAME — lives in `_scalar`
    // alone, which is element 0 of the indexed view; anything that touches another
    // subscript materialises `_indexed` first. A shell session seeds two dozen variables
    // and forks the lot on every command substitution, so what a variable costs when it
    // holds one string is a cost the whole port pays.
    private SortedDictionary<long, string>? _indexed;
    private Dictionary<string, string>? _associative;
    private string? _scalar;

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
                return _associative is not null && _associative.TryGetValue("0", out var assoc)
                    ? assoc
                    : string.Empty;
            }

            if (_indexed is null)
            {
                return _scalar ?? string.Empty;
            }

            return _indexed.TryGetValue(0, out var value) ? value : string.Empty;
        }
    }

    /// <summary>Replaces the variable's value with a scalar.</summary>
    public void SetScalar(string value)
    {
        if (IsAssociative)
        {
            Associative()["0"] = Transform(value);
        }
        else
        {
            _indexed = null;
            _scalar = Transform(value);
        }

        IsUnset = false;
    }

    /// <summary>Appends to the variable's scalar value.</summary>
    public void AppendScalar(string value) => SetScalar(Value + value);

    /// <summary>Sets one element of an indexed array.</summary>
    public void SetIndexed(long index, string value)
    {
        Attributes |= VariableAttributes.IndexedArray;

        // A negative subscript counts back from the end, so `a[-1]=x` replaces the last
        // element rather than creating one at index -1.
        if (index < 0)
        {
            index += IndexedCount == 0 ? 0 : HighestIndex() + 1;
        }

        if (index == 0 && _indexed is null)
        {
            _scalar = Transform(value);
        }
        else
        {
            Indexed()[index] = Transform(value);
        }

        IsUnset = false;
    }

    /// <summary>Sets one element of an associative array.</summary>
    public void SetAssociative(string key, string value)
    {
        Attributes |= VariableAttributes.AssociativeArray;
        Associative()[key] = Transform(value);
        IsUnset = false;
    }

    /// <summary>Replaces the whole variable with an indexed array built from <paramref name="values"/>.</summary>
    public void SetArray(IEnumerable<string> values)
    {
        Attributes |= VariableAttributes.IndexedArray;
        _indexed = null;
        _scalar = null;
        Fill(values, 0);
        IsUnset = false;
    }

    /// <summary>Appends elements to the end of an indexed array.</summary>
    public void AppendArray(IEnumerable<string> values)
    {
        Attributes |= VariableAttributes.IndexedArray;
        Fill(values, IndexedCount == 0 ? 0 : HighestIndex() + 1);
        IsUnset = false;
    }

    // Writes values at consecutive subscripts, keeping the one-element shape for as long
    // as it holds.
    private void Fill(IEnumerable<string> values, long next)
    {
        foreach (var value in values)
        {
            if (next == 0 && _indexed is null)
            {
                _scalar = Transform(value);
            }
            else
            {
                Indexed()[next] = Transform(value);
            }

            next++;
        }
    }

    /// <summary>Reads one element, or <see langword="null"/> when absent.</summary>
    public string? GetElement(string subscript)
    {
        if (IsAssociative)
        {
            return _associative is not null && _associative.TryGetValue(subscript, out var value)
                ? value
                : null;
        }

        if (!long.TryParse(subscript, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return null;
        }

        index = NormalizeIndex(index);

        if (_indexed is null)
        {
            return index == 0 ? _scalar : null;
        }

        return _indexed.TryGetValue(index, out var element) ? element : null;
    }

    /// <summary>Removes one element.</summary>
    public void UnsetElement(string subscript)
    {
        if (IsAssociative)
        {
            _associative?.Remove(subscript);
            return;
        }

        if (!long.TryParse(subscript, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return;
        }

        index = NormalizeIndex(index);

        if (_indexed is null)
        {
            if (index == 0)
            {
                _scalar = null;
            }

            return;
        }

        _indexed.Remove(index);
    }

    /// <summary>Every element value, in subscript order.</summary>
    public IReadOnlyList<string> Elements
    {
        get
        {
            if (IsAssociative)
            {
                return _associative is null ? [] : [.. _associative.Values];
            }

            if (_indexed is null)
            {
                return _scalar is null ? [] : [_scalar];
            }

            return [.. _indexed.Values];
        }
    }

    /// <summary>Every subscript, in order.</summary>
    public IReadOnlyList<string> Keys
    {
        get
        {
            if (IsAssociative)
            {
                return _associative is null ? [] : [.. _associative.Keys];
            }

            if (_indexed is null)
            {
                return _scalar is null ? [] : ["0"];
            }

            return [.. _indexed.Keys.Select(static k => k.ToString(CultureInfo.InvariantCulture))];
        }
    }

    /// <summary>Number of elements. A scalar counts as one.</summary>
    public int Count => IsAssociative ? _associative?.Count ?? 0 : IndexedCount;

    // The indexed view's size, whichever attributes the variable also carries: the
    // subscript arithmetic below is about indexed storage and nothing else.
    private int IndexedCount => _indexed?.Count ?? (_scalar is null ? 0 : 1);

    // A negative subscript counts back from the end, so `${a[-1]}` is the last element.
    private long NormalizeIndex(long index)
    {
        if (index >= 0)
        {
            return index;
        }

        var highest = IndexedCount == 0 ? -1 : HighestIndex();
        return highest + 1 + index;
    }

    // The largest subscript in use. Only the sparse form has to look.
    private long HighestIndex() => _indexed is null ? 0 : _indexed.Keys.Max();

    // Promotes the one-element form to the full map, so a second subscript can be written.
    private SortedDictionary<long, string> Indexed()
    {
        if (_indexed is null)
        {
            _indexed = [];

            if (_scalar is not null)
            {
                _indexed[0] = _scalar;
                _scalar = null;
            }
        }

        return _indexed;
    }

    private Dictionary<string, string> Associative() =>
        _associative ??= new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Copies the variable, for a subshell that must not share it.</summary>
    /// <remarks>
    /// The copy is made from the storage rather than by replaying the setters: replaying
    /// meant rebuilding every subscript from its string form, and a fork copies every
    /// variable in scope, which a command substitution does once per call.
    /// </remarks>
    internal ShellVariable Clone()
    {
        var clone = new ShellVariable(Attributes)
        {
            IsUnset = IsUnset,
            _scalar = _scalar,
        };

        if (_indexed is not null)
        {
            clone._indexed = new SortedDictionary<long, string>(_indexed);
        }

        if (_associative is not null)
        {
            clone._associative = new Dictionary<string, string>(_associative, StringComparer.Ordinal);
        }

        return clone;
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
