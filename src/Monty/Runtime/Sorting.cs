namespace Monty.Runtime;

/// <summary>Implements <c>sorted</c> and <c>list.sort</c>.</summary>
/// <remarks>
/// The sort is stable, as Python's is, and the <c>key</c> function is applied once per
/// element rather than once per comparison — a difference that is observable when the key
/// has side effects, and a large difference in cost when it does not.
/// </remarks>
public static class Sorting
{
    /// <summary>Sorts <paramref name="items"/>, honouring the <c>key</c> and <c>reverse</c> keywords.</summary>
    public static List<PyObject> Sort(VirtualMachine machine, IEnumerable<PyObject> items, PyDict? keywords)
    {
        var source = items.ToList();
        PyObject? key = null;
        var reverse = false;

        if (keywords is not null)
        {
            if (keywords.TryGetValue(new PyStr("key"), out var keyFunction) && keyFunction is not PyNone)
            {
                key = keyFunction;
            }

            if (keywords.TryGetValue(new PyStr("reverse"), out var reverseFlag))
            {
                reverse = reverseFlag.IsTruthy();
            }
        }

        var decorated = new List<(PyObject Key, PyObject Value, int Position)>(source.Count);

        for (var i = 0; i < source.Count; i++)
        {
            decorated.Add((key is null ? source[i] : machine.Call(key, [source[i]]), source[i], i));
        }

        decorated.Sort((left, right) =>
        {
            var comparison = left.Key.PyCompare(right.Key)
                ?? throw new PyRaise(PyErrors.TypeError(
                    $"'<' not supported between instances of '{left.Key.TypeName}' and '{right.Key.TypeName}'"));

            // Falling back to the original position is what makes the sort stable.
            return comparison != 0 ? comparison : left.Position.CompareTo(right.Position);
        });

        var result = decorated.Select(static entry => entry.Value).ToList();

        if (reverse)
        {
            result.Reverse();
        }

        return result;
    }
}
