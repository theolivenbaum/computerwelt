using System.Globalization;

namespace Computerwelt.Emulation.Bash.Builtins.Jq;

/// <summary>
/// The operations jq defines directly on values: indexing, slicing and the arithmetic and
/// comparison operators.
/// </summary>
/// <remarks>
/// jq's operators are polymorphic in ways that look arbitrary until you use them —
/// <c>+</c> concatenates arrays and merges objects, <c>-</c> removes array elements,
/// <c>/</c> splits strings — so each one is spelled out rather than reduced to a numeric
/// core with special cases bolted on.
/// </remarks>
internal static class JqValues
{
    /// <summary>Indexes <paramref name="target"/> by <paramref name="key"/>.</summary>
    public static JsonValue Index(JsonValue target, JsonValue key)
    {
        switch (target)
        {
            case JsonNull:
                // Indexing null yields null, which is what makes `.a.b.c` safe on absent
                // data instead of raising at the first missing level.
                return key is JsonString or JsonNumber or JsonNull
                    ? JsonNull.Instance
                    : throw new JqException($"Cannot index null with {key.TypeName}");

            case JsonObject json when key is JsonString name:
                return json[name.Value] ?? JsonNull.Instance;

            case JsonObject when key is JsonNull:
                return JsonNull.Instance;

            case JsonObject:
                throw new JqException($"Cannot index object with {key.TypeName}");

            case JsonArray array when key is JsonNumber number:
            {
                var index = (int)Math.Floor(number.Value);

                if (index < 0)
                {
                    index += array.Items.Count;
                }

                return index >= 0 && index < array.Items.Count ? array.Items[index] : JsonNull.Instance;
            }

            case JsonArray array when key is JsonArray needle:
                return new JsonArray(Indices(array, needle).Select(static i => (JsonValue)new JsonNumber(i)).ToList());

            case JsonArray when key is JsonNull:
                return JsonNull.Instance;

            case JsonArray when key is JsonString name:
                throw new JqException($"Cannot index array with \"{name.Value}\"");

            case JsonArray:
                throw new JqException($"Cannot index array with {key.TypeName}");

            default:
                throw new JqException(
                    $"Cannot index {target.TypeName} with {(key is JsonString s ? $"\"{s.Value}\"" : key.TypeName)}");
        }
    }

    /// <summary>Slices an array or a string.</summary>
    public static JsonValue Slice(JsonValue target, JsonValue from, JsonValue to)
    {
        if (target is JsonNull)
        {
            return JsonNull.Instance;
        }

        var length = target switch
        {
            JsonArray array => array.Items.Count,
            JsonString text => text.Value.Length,
            _ => throw new JqException($"Cannot index {target.TypeName} with object"),
        };

        var start = Clamp(from, 0, length);
        var end = Clamp(to, length, length);

        if (end < start)
        {
            end = start;
        }

        return target switch
        {
            JsonArray array => new JsonArray(array.Items.GetRange(start, end - start)),
            _ => new JsonString(((JsonString)target).Value[start..end]),
        };
    }

    private static int Clamp(JsonValue bound, int fallback, int length)
    {
        if (bound is not JsonNumber number)
        {
            return fallback;
        }

        var index = (int)Math.Floor(number.Value);

        if (index < 0)
        {
            index += length;
        }

        return Math.Clamp(index, 0, length);
    }

    /// <summary>Applies a binary operator.</summary>
    public static JsonValue Binary(string op, JsonValue left, JsonValue right)
    {
        switch (op)
        {
            case "+": return Add(left, right);
            case "-": return Subtract(left, right);
            case "*": return Multiply(left, right);
            case "/": return Divide(left, right);
            case "%": return Modulo(left, right);
        }

        var order = JsonValue.Compare(left, right);

        return JsonValue.Of(op switch
        {
            "==" => order == 0,
            "!=" => order != 0,
            "<" => order < 0,
            "<=" => order <= 0,
            ">" => order > 0,
            ">=" => order >= 0,
            _ => throw new JqException($"jq: error: unknown operator {op}"),
        });
    }

    private static JsonValue Add(JsonValue left, JsonValue right)
    {
        if (left is JsonNull)
        {
            return right;
        }

        if (right is JsonNull)
        {
            return left;
        }

        switch (left, right)
        {
            case (JsonNumber a, JsonNumber b):
                return new JsonNumber(a.Value + b.Value);

            case (JsonString a, JsonString b):
                return new JsonString(a.Value + b.Value);

            case (JsonArray a, JsonArray b):
                return new JsonArray(a.Items.Concat(b.Items).ToList());

            case (JsonObject a, JsonObject b):
            {
                var merged = new JsonObject(a);

                foreach (var (key, value) in b.Entries())
                {
                    merged.Set(key, value);
                }

                return merged;
            }

            default:
                throw new JqException(
                    $"{JqEvaluator.Describe(left)} and {JqEvaluator.Describe(right)} cannot be added");
        }
    }

    private static JsonValue Subtract(JsonValue left, JsonValue right)
    {
        switch (left, right)
        {
            case (JsonNumber a, JsonNumber b):
                return new JsonNumber(a.Value - b.Value);

            case (JsonArray a, JsonArray b):
                return new JsonArray(
                    a.Items.Where(item => !b.Items.Any(excluded => JsonValue.DeepEquals(item, excluded))).ToList());

            default:
                throw new JqException(
                    $"{JqEvaluator.Describe(left)} and {JqEvaluator.Describe(right)} cannot be subtracted");
        }
    }

    private static JsonValue Multiply(JsonValue left, JsonValue right)
    {
        switch (left, right)
        {
            case (JsonNumber a, JsonNumber b):
                return new JsonNumber(a.Value * b.Value);

            case (JsonString a, JsonNumber b):
                return Repeat(a.Value, b.Value);

            case (JsonNumber a, JsonString b):
                return Repeat(b.Value, a.Value);

            case (JsonObject a, JsonObject b):
                return DeepMerge(a, b);

            default:
                throw new JqException(
                    $"{JqEvaluator.Describe(left)} and {JqEvaluator.Describe(right)} cannot be multiplied");
        }
    }

    /// <summary>Repeats a string, which jq turns into null for a non-positive count.</summary>
    private static JsonValue Repeat(string text, double count)
    {
        var times = (int)count;
        return times <= 0 ? JsonNull.Instance : new JsonString(string.Concat(Enumerable.Repeat(text, times)));
    }

    /// <summary>Merges two objects recursively, which is what <c>*</c> means for objects.</summary>
    private static JsonObject DeepMerge(JsonObject left, JsonObject right)
    {
        var merged = new JsonObject(left);

        foreach (var (key, value) in right.Entries())
        {
            merged.Set(
                key,
                merged[key] is JsonObject existing && value is JsonObject update
                    ? DeepMerge(existing, update)
                    : value);
        }

        return merged;
    }

    private static JsonValue Divide(JsonValue left, JsonValue right)
    {
        switch (left, right)
        {
            case (JsonNumber a, JsonNumber b):
                return b.Value == 0
                    ? throw new JqException(
                        $"{JqEvaluator.Describe(left)} and {JqEvaluator.Describe(right)} cannot be divided because the divisor is zero")
                    : new JsonNumber(a.Value / b.Value);

            case (JsonString a, JsonString b):
                return Split(a.Value, b.Value);

            default:
                throw new JqException(
                    $"{JqEvaluator.Describe(left)} and {JqEvaluator.Describe(right)} cannot be divided");
        }
    }

    private static JsonValue Modulo(JsonValue left, JsonValue right)
    {
        if (left is not JsonNumber a || right is not JsonNumber b)
        {
            throw new JqException(
                $"{JqEvaluator.Describe(left)} and {JqEvaluator.Describe(right)} cannot be divided");
        }

        var divisor = (long)b.Value;

        if (divisor == 0)
        {
            throw new JqException(
                $"{JqEvaluator.Describe(left)} and {JqEvaluator.Describe(right)} cannot be divided because the divisor is zero");
        }

        return new JsonNumber((long)a.Value % divisor);
    }

    /// <summary>Splits a string, treating an empty separator as producing one part per character.</summary>
    public static JsonArray Split(string text, string separator)
    {
        if (separator.Length == 0)
        {
            return new JsonArray(text.Select(static c => (JsonValue)new JsonString(c.ToString())).ToList());
        }

        return new JsonArray(
            text.Split(separator, StringSplitOptions.None).Select(static p => (JsonValue)new JsonString(p)).ToList());
    }

    /// <summary>Renders a value as <c>tostring</c> does: a string stays itself, anything else is JSON.</summary>
    public static string ToText(JsonValue value) =>
        value is JsonString text ? text.Value : JsonWriter.Write(value, JsonFormat.Compact);

    /// <summary>Converts a value to a number, as <c>tonumber</c> does.</summary>
    public static double ToNumber(JsonValue value) => value switch
    {
        JsonNumber number => number.Value,
        JsonString text when double.TryParse(text.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) =>
            parsed,
        JsonString text => throw new JqException($"Cannot parse '{text.Value}' as number"),
        _ => throw new JqException($"{JqEvaluator.Describe(value)} cannot be parsed as a number"),
    };

    /// <summary>The positions at which <paramref name="needle"/> occurs as a subsequence.</summary>
    public static List<int> Indices(JsonArray haystack, JsonArray needle)
    {
        var found = new List<int>();

        if (needle.Items.Count == 0)
        {
            return found;
        }

        for (var i = 0; i + needle.Items.Count <= haystack.Items.Count; i++)
        {
            var matches = true;

            for (var j = 0; j < needle.Items.Count && matches; j++)
            {
                matches = JsonValue.DeepEquals(haystack.Items[i + j], needle.Items[j]);
            }

            if (matches)
            {
                found.Add(i);
            }
        }

        return found;
    }

    /// <summary>
    /// jq's <c>contains</c>: recursive for containers, substring for strings, equality for
    /// everything else.
    /// </summary>
    public static bool Contains(JsonValue haystack, JsonValue needle)
    {
        switch (haystack, needle)
        {
            case (JsonObject a, JsonObject b):
                return b.Entries().All(entry => a[entry.Key] is { } value && Contains(value, entry.Value));

            case (JsonArray a, JsonArray b):
                return b.Items.All(wanted => a.Items.Any(item => Contains(item, wanted)));

            case (JsonString a, JsonString b):
                return a.Value.Contains(b.Value, StringComparison.Ordinal);

            default:
                if (haystack.Kind != needle.Kind)
                {
                    throw new JqException(
                        $"{JqEvaluator.Describe(haystack)} and {JqEvaluator.Describe(needle)} cannot have their containment checked");
                }

                return JsonValue.DeepEquals(haystack, needle);
        }
    }

    /// <summary>Reads the value at <paramref name="path"/>, or null when any step is missing.</summary>
    public static JsonValue GetPath(JsonValue root, IReadOnlyList<JsonValue> path)
    {
        var current = root;

        foreach (var step in path)
        {
            if (current is JsonNull)
            {
                return JsonNull.Instance;
            }

            current = step is JsonObject range ? Slice(current, range["start"] ?? JsonNull.Instance, range["end"] ?? JsonNull.Instance)
                : Index(current, step);
        }

        return current;
    }

    /// <summary>Returns a copy of <paramref name="root"/> with <paramref name="path"/> set.</summary>
    public static JsonValue SetPath(JsonValue root, IReadOnlyList<JsonValue> path, int depth, JsonValue value)
    {
        if (depth >= path.Count)
        {
            return value;
        }

        var step = path[depth];

        switch (step)
        {
            case JsonString name:
            {
                var json = root switch
                {
                    JsonObject existing => new JsonObject(existing),
                    JsonNull => new JsonObject(),
                    _ => throw new JqException($"Cannot index {root.TypeName} with \"{name.Value}\""),
                };

                json.Set(name.Value, SetPath(json[name.Value] ?? JsonNull.Instance, path, depth + 1, value));
                return json;
            }

            case JsonNumber number:
            {
                var array = root switch
                {
                    JsonArray existing => new JsonArray(existing.Items.ToList()),
                    JsonNull => new JsonArray(),
                    _ => throw new JqException($"Cannot index {root.TypeName} with number"),
                };

                var index = (int)number.Value;

                if (index < 0)
                {
                    index += array.Items.Count;

                    if (index < 0)
                    {
                        throw new JqException("Out of bounds negative array index");
                    }
                }

                while (array.Items.Count <= index)
                {
                    array.Items.Add(JsonNull.Instance);
                }

                array.Items[index] = SetPath(array.Items[index], path, depth + 1, value);
                return array;
            }

            case JsonObject range:
            {
                // A slice step, as `path(.[1:3])` produces.
                var array = root switch
                {
                    JsonArray existing => existing,
                    JsonNull => new JsonArray(),
                    _ => throw new JqException($"Cannot update field at object index of {root.TypeName}"),
                };

                var start = Clamp(range["start"] ?? JsonNull.Instance, 0, array.Items.Count);
                var end = Math.Max(Clamp(range["end"] ?? JsonNull.Instance, array.Items.Count, array.Items.Count), start);
                var replacement = SetPath(
                    new JsonArray(array.Items.GetRange(start, end - start)),
                    path,
                    depth + 1,
                    value);

                if (replacement is not JsonArray inserted)
                {
                    throw new JqException("A slice of an array can only be assigned another array");
                }

                var items = array.Items.ToList();
                items.RemoveRange(start, end - start);
                items.InsertRange(start, inserted.Items);
                return new JsonArray(items);
            }

            default:
                throw new JqException($"Invalid path component {step.TypeName}");
        }
    }

    /// <summary>Returns a copy of <paramref name="root"/> with <paramref name="path"/> removed.</summary>
    public static JsonValue DeletePath(JsonValue root, IReadOnlyList<JsonValue> path, int depth)
    {
        if (path.Count == 0)
        {
            return JsonNull.Instance;
        }

        var step = path[depth];
        var last = depth == path.Count - 1;

        switch (root)
        {
            case JsonNull:
                return JsonNull.Instance;

            case JsonObject json when step is JsonString name:
            {
                if (!json.ContainsKey(name.Value))
                {
                    return json;
                }

                var copy = new JsonObject(json);

                if (last)
                {
                    copy.Remove(name.Value);
                }
                else
                {
                    copy.Set(name.Value, DeletePath(copy[name.Value]!, path, depth + 1));
                }

                return copy;
            }

            case JsonArray array when step is JsonNumber number:
            {
                var index = (int)number.Value;

                if (index < 0)
                {
                    index += array.Items.Count;
                }

                if (index < 0 || index >= array.Items.Count)
                {
                    return array;
                }

                var items = array.Items.ToList();

                if (last)
                {
                    items.RemoveAt(index);
                }
                else
                {
                    items[index] = DeletePath(items[index], path, depth + 1);
                }

                return new JsonArray(items);
            }

            case JsonArray array when step is JsonObject range && last:
            {
                var start = Clamp(range["start"] ?? JsonNull.Instance, 0, array.Items.Count);
                var end = Math.Max(Clamp(range["end"] ?? JsonNull.Instance, array.Items.Count, array.Items.Count), start);
                var items = array.Items.ToList();
                items.RemoveRange(start, end - start);
                return new JsonArray(items);
            }

            default:
                throw new JqException($"Cannot delete field at index of {root.TypeName}");
        }
    }
}
