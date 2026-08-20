using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Computerwelt.Emulation.Bash.Builtins.Jq;

/// <summary>
/// The jq builtins that cannot be written in jq itself.
/// </summary>
/// <remarks>
/// Everything definable in the language lives in <see cref="JqPrelude"/> instead, which
/// keeps this file to the primitives: type inspection, string and number operations,
/// regular expressions, path manipulation and the input stream. The split follows jq's own,
/// so a filter's behaviour can be checked against jq's builtin.jq where one exists.
/// </remarks>
internal static class JqBuiltins
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Calls a builtin, or reports that no such filter exists.</summary>
    public static IEnumerable<JsonValue> Call(JqEvaluator evaluator, JqCall node, JsonValue input, JqScope scope)
    {
        var arity = node.Arguments.Count;

        // Filters whose arguments must stay unevaluated — because they are applied to a
        // different input, or not at all — are dispatched before anything is evaluated.
        switch (node.Name, arity)
        {
            case ("empty", 0):
                return [];

            case ("error", 0):
                throw new JqException(input);

            case ("path", 1):
                return JqPaths.Evaluate(evaluator, node.Arguments[0], new JqPath([], input), input, scope)
                    .Select(static step => (JsonValue)new JsonArray(step.Path.ToList()));

            case ("limit", 2):
                return evaluator.Eval(node.Arguments[0], input, scope).SelectMany(count =>
                    evaluator.Eval(node.Arguments[1], input, scope)
                        .Take(Math.Max((int)JqValues.ToNumber(count), 0)));

            case ("first", 1):
                return evaluator.Eval(node.Arguments[0], input, scope).Take(1);

            case ("last", 1):
                return evaluator.Eval(node.Arguments[0], input, scope).TakeLast(1);

            case ("isempty", 1):
                return [JsonValue.Of(!evaluator.Eval(node.Arguments[0], input, scope).Any())];

            case ("sub", 2) or ("sub", 3) or ("gsub", 2) or ("gsub", 3):
                return Substitute(evaluator, node, input, scope);

            case ("getpath", 1):
                return evaluator.Eval(node.Arguments[0], input, scope).Select(path =>
                    path is JsonArray steps
                        ? JqValues.GetPath(input, steps.Items)
                        : throw new JqException("Path must be specified as an array"));

            case ("input", 0):
                return [NextInput(evaluator.Runtime)];

            case ("inputs", 0):
                return AllInputs(evaluator.Runtime);

            case ("sort_by", 1) or ("group_by", 1) or ("unique_by", 1) or ("min_by", 1) or ("max_by", 1):
                return ByKey(evaluator, node, input, scope);
        }

        var arguments = node.Arguments.Select(argument => evaluator.Eval(argument, input, scope).ToList()).ToList();
        return Product(arguments, 0, [], values => Invoke(evaluator, node.Name, values, input));
    }

    /// <summary>
    /// Calls <paramref name="body"/> once per combination of argument values.
    /// </summary>
    /// <remarks>
    /// jq arguments are streams, and a builtin with two multi-valued arguments runs once for
    /// every pair — <c>range(1,2; 3,4)</c> produces four ranges, not two.
    /// </remarks>
    private static IEnumerable<JsonValue> Product(
        List<List<JsonValue>> arguments,
        int index,
        List<JsonValue> chosen,
        Func<IReadOnlyList<JsonValue>, IEnumerable<JsonValue>> body)
    {
        if (index >= arguments.Count)
        {
            return body(chosen);
        }

        return arguments[index].SelectMany(value =>
            Product(arguments, index + 1, [.. chosen, value], body));
    }

    private static IEnumerable<JsonValue> Invoke(
        JqEvaluator evaluator,
        string name,
        IReadOnlyList<JsonValue> args,
        JsonValue input)
    {
        switch (name, args.Count)
        {
            case ("type", 0): return [new JsonString(input.TypeName)];
            case ("length", 0): return [Length(input)];
            case ("utf8bytelength", 0): return [new JsonNumber(Encoding.UTF8.GetByteCount(Text(input, name)))];
            case ("keys", 0): return [Keys(input, sorted: true)];
            case ("keys_unsorted", 0): return [Keys(input, sorted: false)];
            case ("tostring", 0): return [new JsonString(JqValues.ToText(input))];
            case ("tonumber", 0): return [new JsonNumber(JqValues.ToNumber(input))];
            case ("tojson", 0): return [new JsonString(JsonWriter.Write(input, JsonFormat.Compact))];
            case ("fromjson", 0): return [JsonReader.Parse(Text(input, name))];
            case ("error", 1): throw new JqException(args[0]);
            case ("has", 1): return [JsonValue.Of(Has(input, args[0]))];
            case ("contains", 1): return [JsonValue.Of(JqValues.Contains(input, args[0]))];

            case ("startswith", 1):
                return [JsonValue.Of(Text(input, name).StartsWith(Text(args[0], name), StringComparison.Ordinal))];

            case ("endswith", 1):
                return [JsonValue.Of(Text(input, name).EndsWith(Text(args[0], name), StringComparison.Ordinal))];

            case ("ltrimstr", 1):
                return [input is JsonString text && args[0] is JsonString prefix
                    && text.Value.StartsWith(prefix.Value, StringComparison.Ordinal)
                    ? new JsonString(text.Value[prefix.Value.Length..])
                    : input];

            case ("rtrimstr", 1):
                return [input is JsonString text2 && args[0] is JsonString suffix
                    && suffix.Value.Length > 0
                    && text2.Value.EndsWith(suffix.Value, StringComparison.Ordinal)
                    ? new JsonString(text2.Value[..^suffix.Value.Length])
                    : input];

            case ("ltrimstr", 0) or ("trim", 0): return [new JsonString(Text(input, name).Trim())];
            case ("ascii_downcase", 0): return [new JsonString(MapAscii(Text(input, name), char.ToLowerInvariant))];
            case ("ascii_upcase", 0): return [new JsonString(MapAscii(Text(input, name), char.ToUpperInvariant))];
            case ("explode", 0): return [Explode(Text(input, name))];
            case ("implode", 0): return [Implode(input)];
            case ("split", 1): return [SplitLiteral(input, args[0])];
            case ("reverse", 0): return [Reverse(input)];
            case ("sort", 0): return [Sort(input)];
            case ("min", 0): return [Extreme(input, smallest: true)];
            case ("max", 0): return [Extreme(input, smallest: false)];
            case ("flatten", 1): return [Flatten(input, (int)JqValues.ToNumber(args[0]))];
            case ("indices", 1): return [Indices(input, args[0])];
            case ("setpath", 2): return [SetPath(input, args[0], args[1])];
            case ("delpaths", 1): return [DeletePaths(input, args[0])];
            case ("range", 2): return Range(JqValues.ToNumber(args[0]), JqValues.ToNumber(args[1]), 1);
            case ("range", 3): return Range(JqValues.ToNumber(args[0]), JqValues.ToNumber(args[1]), JqValues.ToNumber(args[2]));

            case ("floor", 0): return [new JsonNumber(Math.Floor(JqValues.ToNumber(input)))];
            case ("ceil", 0): return [new JsonNumber(Math.Ceiling(JqValues.ToNumber(input)))];
            case ("round", 0): return [new JsonNumber(Math.Round(JqValues.ToNumber(input), MidpointRounding.AwayFromZero))];
            case ("fabs", 0): return [new JsonNumber(Math.Abs(JqValues.ToNumber(input)))];
            case ("abs", 0): return [Absolute(input)];
            case ("sqrt", 0): return [new JsonNumber(Math.Sqrt(JqValues.ToNumber(input)))];
            case ("exp", 0): return [new JsonNumber(Math.Exp(JqValues.ToNumber(input)))];
            case ("exp2", 0): return [new JsonNumber(Math.Pow(2, JqValues.ToNumber(input)))];
            case ("exp10", 0): return [new JsonNumber(Math.Pow(10, JqValues.ToNumber(input)))];
            case ("log", 0): return [new JsonNumber(Math.Log(JqValues.ToNumber(input)))];
            case ("log2", 0): return [new JsonNumber(Math.Log2(JqValues.ToNumber(input)))];
            case ("log10", 0): return [new JsonNumber(Math.Log10(JqValues.ToNumber(input)))];
            case ("pow", 2): return [new JsonNumber(Math.Pow(JqValues.ToNumber(args[0]), JqValues.ToNumber(args[1])))];
            case ("infinite", 0): return [new JsonNumber(double.PositiveInfinity)];
            case ("nan", 0): return [new JsonNumber(double.NaN)];
            case ("isnan", 0): return [JsonValue.Of(input is JsonNumber n && double.IsNaN(n.Value))];
            case ("isinfinite", 0): return [JsonValue.Of(input is JsonNumber i && double.IsInfinity(i.Value))];

            case ("isnormal", 0):
                return [JsonValue.Of(input is JsonNumber v && double.IsNormal(v.Value))];

            case ("test", 1) or ("test", 2):
                return [JsonValue.Of(Regex(args, input, name).IsMatch(Text(input, name)))];

            case ("match", 1) or ("match", 2):
                return Matches(input, args, name).Select(MatchObject);

            case ("capture", 1) or ("capture", 2):
                return Matches(input, args, name).Select(match => Captures(match, Regex(args, input, name)));

            case ("scan", 1) or ("scan", 2):
                return Matches(input, args, name).Select(static match => match.Groups.Count > 1
                    ? (JsonValue)new JsonArray(Enumerable.Range(1, match.Groups.Count - 1)
                        .Select(g => (JsonValue)new JsonString(match.Groups[g].Value)).ToList())
                    : new JsonString(match.Value));

            case ("splits", 1) or ("splits", 2) or ("split", 2):
            {
                var parts = SplitRegex(Text(input, name), Regex(args, input, name));
                return name == "split" ? [new JsonArray(parts)] : parts;
            }

            case ("now", 0):
                return [new JsonNumber(evaluator.Runtime.Now)];

            case ("todate", 0) or ("todateiso8601", 0):
                return [new JsonString(DateTimeOffset.FromUnixTimeSeconds((long)JqValues.ToNumber(input))
                    .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))];

            case ("fromdate", 0) or ("fromdateiso8601", 0):
                return [new JsonNumber(ParseDate(Text(input, name)))];

            case ("debug", 0):
                evaluator.Runtime.Stderr.Append(CultureInfo.InvariantCulture, $"[\"DEBUG:\",{input}]\n");
                return [input];

            case ("stderr", 0):
                evaluator.Runtime.Stderr.Append(input.ToString());
                return [input];

            case ("input_line_number", 0):
                return [new JsonNumber(evaluator.Runtime.InputNumber)];

            case ("input_filename", 0):
                return [evaluator.Runtime.Filename is { } file ? new JsonString(file) : JsonNull.Instance];

            case ("halt", 0):
                throw new JqHaltException(0, null);

            case ("halt_error", 0):
                throw new JqHaltException(5, input);

            case ("halt_error", 1):
                throw new JqHaltException((int)JqValues.ToNumber(args[0]), input);

            case ("getpath", 1):
                return [args[0] is JsonArray steps
                    ? JqValues.GetPath(input, steps.Items)
                    : throw new JqException("Path must be specified as an array")];

            default:
                throw new JqException($"{name}/{args.Count} is not defined");
        }
    }

    /// <summary>
    /// The <c>_by</c> family, which order or group by a filter's output rather than by the
    /// element itself.
    /// </summary>
    /// <remarks>
    /// The key is the <i>array</i> of everything the filter yields, not its first value, so
    /// <c>sort_by(.a, .b)</c> orders by both fields in turn — and comparing arrays gives
    /// that lexicographic tie-break for free.
    /// </remarks>
    private static IEnumerable<JsonValue> ByKey(JqEvaluator evaluator, JqCall node, JsonValue input, JqScope scope)
    {
        if (input is not JsonArray array)
        {
            throw new JqException($"Cannot index {input.TypeName} with number");
        }

        var keyed = array.Items
            .Select(item => (Key: (JsonValue)new JsonArray(evaluator.Eval(node.Arguments[0], item, scope).ToList()), Item: item))
            .ToList();

        switch (node.Name)
        {
            case "min_by" or "max_by":
            {
                if (keyed.Count == 0)
                {
                    return [JsonNull.Instance];
                }

                var best = keyed[0];

                foreach (var candidate in keyed.Skip(1))
                {
                    var order = JsonValue.Compare(candidate.Key, best.Key);

                    if (node.Name == "min_by" ? order < 0 : order >= 0)
                    {
                        best = candidate;
                    }
                }

                return [best.Item];
            }
        }

        // A stable sort keeps equal keys in input order, which `group_by` relies on.
        var sorted = keyed.OrderBy(static entry => entry.Key, Comparer<JsonValue>.Create(JsonValue.Compare)).ToList();

        if (node.Name == "sort_by")
        {
            return [new JsonArray(sorted.Select(static entry => entry.Item).ToList())];
        }

        var groups = new List<JsonValue>();
        var current = new List<JsonValue>();
        JsonValue? currentKey = null;

        foreach (var entry in sorted)
        {
            if (currentKey is not null && JsonValue.Compare(currentKey, entry.Key) == 0)
            {
                current.Add(entry.Item);
                continue;
            }

            if (currentKey is not null)
            {
                groups.Add(new JsonArray(current));
                current = [];
            }

            currentKey = entry.Key;
            current.Add(entry.Item);
        }

        if (currentKey is not null)
        {
            groups.Add(new JsonArray(current));
        }

        return node.Name switch
        {
            "group_by" => [new JsonArray(groups)],
            _ => [new JsonArray(groups.Select(static group => ((JsonArray)group).Items[0]).ToList())],
        };
    }

    // ---- value helpers ----

    private static string Text(JsonValue value, string name) =>
        value is JsonString text
            ? text.Value
            : throw new JqException($"{JqEvaluator.Describe(value)} cannot be matched, as it is not a string");

    private static JsonValue Length(JsonValue value) => value switch
    {
        JsonNull => new JsonNumber(0),
        JsonBool => throw new JqException($"{JqEvaluator.Describe(value)} has no length"),
        JsonNumber number => new JsonNumber(Math.Abs(number.Value)),

        // Strings measure in codepoints, so an astral character counts once.
        JsonString text => new JsonNumber(CodepointCount(text.Value)),
        JsonArray array => new JsonNumber(array.Items.Count),
        _ => new JsonNumber(((JsonObject)value).Count),
    };

    private static int CodepointCount(string text)
    {
        var count = 0;

        for (var i = 0; i < text.Length; i++)
        {
            count++;

            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
        }

        return count;
    }

    private static JsonValue Absolute(JsonValue value) =>
        value is JsonNumber number ? new JsonNumber(Math.Abs(number.Value)) : value;

    private static JsonValue Keys(JsonValue value, bool sorted) => value switch
    {
        JsonObject json => new JsonArray(
            (sorted ? json.Keys.Order(StringComparer.Ordinal) : json.Keys.AsEnumerable())
                .Select(static key => (JsonValue)new JsonString(key)).ToList()),

        JsonArray array => new JsonArray(
            Enumerable.Range(0, array.Items.Count).Select(static i => (JsonValue)new JsonNumber(i)).ToList()),

        _ => throw new JqException($"{JqEvaluator.Describe(value)} has no keys"),
    };

    private static bool Has(JsonValue value, JsonValue key) => (value, key) switch
    {
        (JsonObject json, JsonString name) => json.ContainsKey(name.Value),
        (JsonArray array, JsonNumber index) => index.Value >= 0 && index.Value < array.Items.Count,
        _ => throw new JqException(
            $"Cannot check whether {value.TypeName} has a {key.TypeName} key"),
    };

    private static string MapAscii(string text, Func<char, char> map) =>
        string.Create(text.Length, text, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsAscii(source[i]) ? map(source[i]) : source[i];
            }
        });

    private static JsonValue Explode(string text)
    {
        var codepoints = new List<JsonValue>();

        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codepoints.Add(new JsonNumber(char.ConvertToUtf32(text[i], text[i + 1])));
                i++;
                continue;
            }

            codepoints.Add(new JsonNumber(text[i]));
        }

        return new JsonArray(codepoints);
    }

    private static JsonValue Implode(JsonValue value)
    {
        if (value is not JsonArray array)
        {
            throw new JqException($"Cannot implode {JqEvaluator.Describe(value)}");
        }

        var builder = new StringBuilder(array.Items.Count);

        foreach (var item in array.Items)
        {
            var code = (int)JqValues.ToNumber(item);

            if (code is < 0 or > 0x10FFFF)
            {
                throw new JqException($"Invalid codepoint literal {code}");
            }

            builder.Append(char.ConvertFromUtf32(code));
        }

        return new JsonString(builder.ToString());
    }

    private static JsonValue SplitLiteral(JsonValue input, JsonValue separator)
    {
        if (input is not JsonString text || separator is not JsonString needle)
        {
            throw new JqException("split input and separator must be strings");
        }

        return JqValues.Split(text.Value, needle.Value);
    }

    private static JsonValue Reverse(JsonValue value) => value switch
    {
        JsonArray array => new JsonArray(Enumerable.Reverse(array.Items).ToList()),
        JsonString text => new JsonString(new string(text.Value.Reverse().ToArray())),
        JsonNull => new JsonArray(),
        _ => throw new JqException($"Cannot reverse {JqEvaluator.Describe(value)}"),
    };

    private static JsonValue Sort(JsonValue value)
    {
        if (value is not JsonArray array)
        {
            throw new JqException($"{JqEvaluator.Describe(value)} cannot be sorted, as it is not an array");
        }

        var items = array.Items.ToList();
        items.Sort(JsonValue.Compare);
        return new JsonArray(items);
    }

    private static JsonValue Extreme(JsonValue value, bool smallest)
    {
        if (value is not JsonArray array)
        {
            throw new JqException($"Cannot compute the {(smallest ? "minimum" : "maximum")} of {value.TypeName}");
        }

        if (array.Items.Count == 0)
        {
            return JsonNull.Instance;
        }

        var best = array.Items[0];

        foreach (var item in array.Items.Skip(1))
        {
            var order = JsonValue.Compare(item, best);

            if (smallest ? order < 0 : order >= 0)
            {
                best = item;
            }
        }

        return best;
    }

    private static JsonValue Flatten(JsonValue value, int depth)
    {
        if (depth < 0)
        {
            throw new JqException("flatten depth must not be negative");
        }

        if (value is not JsonArray array)
        {
            throw new JqException($"Cannot flatten {JqEvaluator.Describe(value)}");
        }

        var flat = new List<JsonValue>();

        foreach (var item in array.Items)
        {
            if (depth > 0 && item is JsonArray nested)
            {
                flat.AddRange(((JsonArray)Flatten(nested, depth - 1)).Items);
                continue;
            }

            flat.Add(item);
        }

        return new JsonArray(flat);
    }

    private static JsonValue Indices(JsonValue input, JsonValue needle)
    {
        switch (input, needle)
        {
            case (JsonNull, _):
                return JsonNull.Instance;

            case (JsonString text, JsonString search):
            {
                var found = new List<JsonValue>();

                if (search.Value.Length == 0)
                {
                    return new JsonArray(found);
                }

                for (var i = text.Value.IndexOf(search.Value, StringComparison.Ordinal);
                     i >= 0;
                     i = text.Value.IndexOf(search.Value, i + 1, StringComparison.Ordinal))
                {
                    found.Add(new JsonNumber(i));
                }

                return new JsonArray(found);
            }

            case (JsonArray array, JsonArray sequence):
                return new JsonArray(
                    JqValues.Indices(array, sequence).Select(static i => (JsonValue)new JsonNumber(i)).ToList());

            case (JsonArray array, _):
            {
                var found = new List<JsonValue>();

                for (var i = 0; i < array.Items.Count; i++)
                {
                    if (JsonValue.DeepEquals(array.Items[i], needle))
                    {
                        found.Add(new JsonNumber(i));
                    }
                }

                return new JsonArray(found);
            }

            default:
                throw new JqException($"Cannot find indices in {JqEvaluator.Describe(input)}");
        }
    }

    private static JsonValue SetPath(JsonValue input, JsonValue path, JsonValue value)
    {
        if (path is not JsonArray steps)
        {
            throw new JqException("Path must be specified as an array");
        }

        return JqValues.SetPath(input, steps.Items, 0, value);
    }

    private static JsonValue DeletePaths(JsonValue input, JsonValue paths)
    {
        if (paths is not JsonArray list)
        {
            throw new JqException("Paths must be specified as an array");
        }

        // Deleting from the end backwards keeps earlier paths valid: removing element 0
        // would otherwise shift every later index.
        var ordered = list.Items.OfType<JsonArray>().ToList();
        ordered.Sort(static (a, b) => JsonValue.Compare(b, a));

        var result = input;

        foreach (var path in ordered)
        {
            result = path.Items.Count == 0 ? JsonNull.Instance : JqValues.DeletePath(result, path.Items, 0);
        }

        return result;
    }

    private static IEnumerable<JsonValue> Range(double from, double to, double by)
    {
        if (by == 0)
        {
            yield break;
        }

        if (by > 0)
        {
            for (var value = from; value < to; value += by)
            {
                yield return new JsonNumber(value);
            }

            yield break;
        }

        for (var value = from; value > to; value += by)
        {
            yield return new JsonNumber(value);
        }
    }

    private static double ParseDate(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : throw new JqException($"date \"{text}\" does not match format \"%Y-%m-%dT%H:%M:%SZ\"");

    // ---- regular expressions ----

    private static Regex Regex(IReadOnlyList<JsonValue> args, JsonValue input, string name)
    {
        var pattern = args.Count > 0 ? args[0] : JsonNull.Instance;
        var flags = args.Count > 1 && args[1] is JsonString text ? text.Value : string.Empty;

        // `match(["re", "flags"])` is the array form every regex builtin also accepts.
        if (pattern is JsonArray form)
        {
            flags = form.Items.Count > 1 ? JqValues.ToText(form.Items[1]) : flags;
            pattern = form.Items.Count > 0 ? form.Items[0] : JsonNull.Instance;
        }

        var options = RegexOptions.None;

        foreach (var flag in flags)
        {
            options |= flag switch
            {
                'g' or 'n' or 'l' => RegexOptions.None,
                'i' => RegexOptions.IgnoreCase,
                'x' => RegexOptions.IgnorePatternWhitespace,
                's' => RegexOptions.Singleline,
                'm' => RegexOptions.Multiline,
                'p' => RegexOptions.Singleline | RegexOptions.Multiline,
                _ => throw new JqException($"{flags} is not a valid modifier string"),
            };
        }

        try
        {
            return new Regex(Text(pattern, name), options, MatchTimeout);
        }
        catch (ArgumentException exception)
        {
            throw new JqException($"{pattern} is not a valid regex: {exception.Message}");
        }
    }

    private static bool IsGlobal(IReadOnlyList<JsonValue> args)
    {
        var flags = args.Count > 1 && args[1] is JsonString text ? text.Value : string.Empty;

        if (args.Count > 0 && args[0] is JsonArray form && form.Items.Count > 1)
        {
            flags = JqValues.ToText(form.Items[1]);
        }

        return flags.Contains('g', StringComparison.Ordinal);
    }

    private static List<Match> Matches(JsonValue input, IReadOnlyList<JsonValue> args, string name)
    {
        var subject = Text(input, name);
        var regex = Regex(args, input, name);

        // `scan` is inherently global; the others need the `g` flag.
        var global = name == "scan" || IsGlobal(args);
        var matches = new List<Match>();
        var position = 0;

        while (position <= subject.Length)
        {
            var match = regex.Match(subject, position);

            if (!match.Success)
            {
                break;
            }

            matches.Add(match);

            if (!global)
            {
                break;
            }

            position = match.Length == 0 ? match.Index + 1 : match.Index + match.Length;
        }

        return matches;
    }

    private static JsonValue MatchObject(Match match)
    {
        var captures = new List<JsonValue>();

        for (var group = 1; group < match.Groups.Count; group++)
        {
            var captured = match.Groups[group];
            var entry = new JsonObject();
            entry.Set("offset", new JsonNumber(captured.Success ? captured.Index : -1));
            entry.Set("length", new JsonNumber(captured.Success ? captured.Length : 0));
            entry.Set("string", captured.Success ? new JsonString(captured.Value) : JsonNull.Instance);

            // A group named only by its number reports a null name, as jq does.
            entry.Set(
                "name",
                int.TryParse(captured.Name, CultureInfo.InvariantCulture, out _)
                    ? JsonNull.Instance
                    : new JsonString(captured.Name));

            captures.Add(entry);
        }

        var result = new JsonObject();
        result.Set("offset", new JsonNumber(match.Index));
        result.Set("length", new JsonNumber(match.Length));
        result.Set("string", new JsonString(match.Value));
        result.Set("captures", new JsonArray(captures));
        return result;
    }

    private static JsonValue Captures(Match match, Regex regex)
    {
        var result = new JsonObject();

        foreach (var groupName in regex.GetGroupNames())
        {
            if (int.TryParse(groupName, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            var group = match.Groups[groupName];
            result.Set(groupName, group.Success ? new JsonString(group.Value) : JsonNull.Instance);
        }

        return result;
    }

    private static List<JsonValue> SplitRegex(string subject, Regex regex)
    {
        var parts = new List<JsonValue>();
        var position = 0;

        foreach (Match match in regex.Matches(subject))
        {
            parts.Add(new JsonString(subject[position..match.Index]));
            position = match.Index + Math.Max(match.Length, 1);
        }

        parts.Add(new JsonString(position <= subject.Length ? subject[position..] : string.Empty));
        return parts;
    }

    /// <summary>
    /// <c>sub</c> and <c>gsub</c>, whose replacement is a filter rather than a value.
    /// </summary>
    /// <remarks>
    /// The replacement runs with <c>.</c> bound to the object of named captures, which is
    /// what makes <c>gsub("(?&lt;x&gt;.)"; "\(.x)!")</c> work — so it cannot be evaluated
    /// before the match is known.
    /// </remarks>
    private static IEnumerable<JsonValue> Substitute(
        JqEvaluator evaluator,
        JqCall node,
        JsonValue input,
        JqScope scope)
    {
        var flagArguments = new List<JsonValue> { evaluator.Eval(node.Arguments[0], input, scope).First() };

        if (node.Arguments.Count > 2)
        {
            flagArguments.Add(evaluator.Eval(node.Arguments[2], input, scope).First());
        }

        var subject = Text(input, node.Name);
        var regex = Regex(flagArguments, input, node.Name);
        var global = node.Name == "gsub" || IsGlobal(flagArguments);

        var builder = new StringBuilder();
        var position = 0;

        while (position <= subject.Length)
        {
            var match = regex.Match(subject, position);

            if (!match.Success)
            {
                break;
            }

            builder.Append(subject, position, match.Index - position);

            var replacement = evaluator.Eval(node.Arguments[1], Captures(match, regex), scope).FirstOrDefault();
            builder.Append(replacement is null ? string.Empty : JqValues.ToText(replacement));

            var advance = match.Index + Math.Max(match.Length, 1);

            // An empty match still consumes the character it sat before, or the scan would
            // never terminate.
            if (match.Length == 0 && match.Index < subject.Length)
            {
                builder.Append(subject[match.Index]);
            }

            position = advance;

            if (!global)
            {
                break;
            }
        }

        builder.Append(position <= subject.Length ? subject[position..] : string.Empty);
        return [new JsonString(builder.ToString())];
    }

    // ---- inputs ----

    private static JsonValue NextInput(JqRuntime runtime)
    {
        if (runtime.Inputs is null || !runtime.Inputs.MoveNext())
        {
            throw new JqException("No more inputs");
        }

        runtime.InputNumber++;
        return runtime.Inputs.Current;
    }

    private static IEnumerable<JsonValue> AllInputs(JqRuntime runtime)
    {
        if (runtime.Inputs is null)
        {
            yield break;
        }

        while (runtime.Inputs.MoveNext())
        {
            runtime.InputNumber++;
            yield return runtime.Inputs.Current;
        }
    }
}

/// <summary>Thrown by <c>halt</c> and <c>halt_error</c> to end the program immediately.</summary>
/// <param name="exitCode">The status to exit with.</param>
/// <param name="payload">What <c>halt_error</c> should print, or null for a silent halt.</param>
internal sealed class JqHaltException(int exitCode, JsonValue? payload) : Exception("halt")
{
    /// <summary>The status to exit with.</summary>
    public int ExitCode { get; } = exitCode;

    /// <summary>What to print before exiting, if anything.</summary>
    public JsonValue? Payload { get; } = payload;
}
