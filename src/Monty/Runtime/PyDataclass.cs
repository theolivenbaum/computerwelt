namespace Monty.Runtime;

/// <summary>
/// A record-like object handed in by the host, or built by <c>@dataclass</c>.
/// </summary>
/// <remarks>
/// This is the shape the host boundary uses for structured values: named fields, an
/// identity, and a frozen flag. Frozen instances refuse attribute assignment, which is how
/// a host passes data in without the sandbox being able to mutate the host's view of it.
/// </remarks>
public sealed class PyDataclass : PyObject
{
    private readonly Dictionary<string, PyObject> _fields;

    /// <summary>
    /// Methods the host exposes on this value, by name.
    /// </summary>
    /// <remarks>
    /// A host record is often more than data — it carries behaviour the sandbox is meant to
    /// call. Each entry is bound to the receiver on lookup, so it reaches Python as an
    /// ordinary method.
    /// </remarks>
    public IReadOnlyDictionary<string, Func<PyDataclass, PyObject[], PyDict?, PyObject>> Methods { get; init; }
        = new Dictionary<string, Func<PyDataclass, PyObject[], PyDict?, PyObject>>(StringComparer.Ordinal);

    /// <summary>Creates a dataclass instance.</summary>
    public PyDataclass(string name, IReadOnlyList<string> fieldNames, IReadOnlyList<PyObject> values, bool frozen)
    {
        Name = name;
        FieldNames = fieldNames;
        Frozen = frozen;
        _fields = new Dictionary<string, PyObject>(StringComparer.Ordinal);

        for (var i = 0; i < fieldNames.Count && i < values.Count; i++)
        {
            _fields[fieldNames[i]] = values[i];
        }
    }

    /// <summary>The class name.</summary>
    public string Name { get; }

    /// <summary>The field names, in declaration order.</summary>
    public IReadOnlyList<string> FieldNames { get; }

    /// <summary>True when attribute assignment is refused.</summary>
    public bool Frozen { get; }

    /// <inheritdoc />
    public override string TypeName => Name;

    /// <inheritdoc />
    /// <remarks>
    /// Only the declared fields are rendered: an attribute set on the instance afterwards
    /// is not part of what the class says it is.
    /// </remarks>
    public override string Repr()
    {
        if (!RecursionGuard.TryEnter(this))
        {
            return "...";
        }

        try
        {
            return Name + "(" + string.Join(", ", FieldNames.Select(field => $"{field}={_fields[field].Repr()}")) + ")";
        }
        finally
        {
            RecursionGuard.Exit(this);
        }
    }

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name)
    {
        if (_fields.TryGetValue(name, out var value))
        {
            return value;
        }

        return Methods.TryGetValue(name, out var method)
            ? new PyBoundMethod(name, this, (self, arguments, keywords) => method((PyDataclass)self, arguments, keywords))
            : null;
    }

    /// <inheritdoc />
    public override bool SetAttribute(string name, PyObject value)
    {
        if (Frozen)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.AttributeError, $"cannot assign to field '{name}'"));
        }

        // An unfrozen instance takes new attributes as any object does; only the declared
        // fields take part in the repr and in equality.
        _fields[name] = value;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A frozen instance hashes by its fields, which is the point of freezing it; an
    /// unfrozen one would break the dict invariant the moment a field changed.
    /// </remarks>
    public override System.Numerics.BigInteger PyHash()
    {
        if (!Frozen)
        {
            throw new PyRaise(PyErrors.TypeError($"unhashable type: '{Name}'"));
        }

        var hash = new System.Numerics.BigInteger(17);

        foreach (var field in FieldNames)
        {
            hash = (hash * 31) + _fields[field].PyHash();
        }

        return hash;
    }

    /// <inheritdoc />
    public override bool PyEquals(PyObject other)
    {
        if (other is not PyDataclass dataclass
            || !string.Equals(Name, dataclass.Name, StringComparison.Ordinal)
            || FieldNames.Count != dataclass.FieldNames.Count)
        {
            return false;
        }

        RecursionGuard.EnterComparison(this);

        try
        {
            return FieldNames.All(field =>
                dataclass._fields.TryGetValue(field, out var value) && SameOrEqual(_fields[field], value));
        }
        finally
        {
            RecursionGuard.Exit(this);
        }
    }
}
