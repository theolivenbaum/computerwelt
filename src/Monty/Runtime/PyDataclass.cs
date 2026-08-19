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
    public override string Repr() =>
        Name + "(" + string.Join(", ", FieldNames.Select(field => $"{field}={_fields[field].Repr()}")) + ")";

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => _fields.GetValueOrDefault(name);

    /// <inheritdoc />
    public override bool SetAttribute(string name, PyObject value)
    {
        if (Frozen)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.AttributeError, $"cannot assign to field '{name}'"));
        }

        if (!_fields.ContainsKey(name))
        {
            return false;
        }

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

        return FieldNames.All(field =>
            dataclass._fields.TryGetValue(field, out var value) && _fields[field].PyEquals(value));
    }
}
