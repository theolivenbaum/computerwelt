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

        var decorated = new List<Entry>(source.Count);

        foreach (var item in source)
        {
            decorated.Add(new Entry(key is null ? item : machine.Call(key, [item]), item));
        }

        var result = MergeSort(decorated).Select(static entry => entry.Value).ToList();

        if (reverse)
        {
            result.Reverse();
        }

        return result;
    }

    /// <summary>
    /// A stable bottom-up merge sort of the decorated entries.
    /// </summary>
    /// <remarks>
    /// <c>List.Sort</c> cannot be used for two reasons: it wraps an exception thrown by the
    /// comparison in an <c>InvalidOperationException</c>, hiding the Python error, and it
    /// gives no control over which operand lands on the left of a failed <c>&lt;</c> —
    /// which the error message names.
    /// </remarks>
    private static List<Entry> MergeSort(List<Entry> items)
    {
        var source = items;
        var target = new List<Entry>(source);

        for (var width = 1; width < source.Count; width *= 2)
        {
            for (var start = 0; start < source.Count; start += 2 * width)
            {
                var middle = Math.Min(start + width, source.Count);
                var end = Math.Min(start + (2 * width), source.Count);
                Merge(source, target, start, middle, end);
            }

            (source, target) = (target, source);
        }

        return source;
    }

    private static void Merge(List<Entry> source, List<Entry> target, int start, int middle, int end)
    {
        var left = start;
        var right = middle;

        for (var i = start; i < end; i++)
        {
            // Taking from the left run unless the right one is strictly smaller keeps the
            // sort stable — and asks `right < left`, which is the order CPython compares in.
            target[i] = left < middle && (right >= end || !Less(source[right].Key, source[left].Key))
                ? source[left++]
                : source[right++];
        }
    }

    private static bool Less(PyObject right, PyObject left) =>
        (right.PyCompare(left)
            ?? throw new PyRaise(PyErrors.TypeError(
                $"'<' not supported between instances of '{right.TypeName}' and '{left.TypeName}'"))) < 0;

    /// <summary>One element, paired with its sort key.</summary>
    private readonly record struct Entry(PyObject Key, PyObject Value);
}
