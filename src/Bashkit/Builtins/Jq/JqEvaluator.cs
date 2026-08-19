using System.Text;

namespace Bashkit.Builtins.Jq;

/// <summary>
/// Everything one jq run can see beyond its input value.
/// </summary>
/// <param name="Environment">The object <c>env</c> and <c>$ENV</c> report.</param>
/// <param name="Budget">The budget charged as values are produced.</param>
internal sealed record JqRuntime(JsonObject Environment, ExecutionBudget Budget)
{
    /// <summary>The remaining inputs <c>input</c> and <c>inputs</c> draw from.</summary>
    public IEnumerator<JsonValue>? Inputs { get; init; }

    /// <summary>The file the current input came from, or null for standard input.</summary>
    public string? Filename { get; set; }

    /// <summary>The 1-based index of the input being processed.</summary>
    public int InputNumber { get; set; }

    /// <summary>The Unix time <c>now</c> reports, fixed for the whole run.</summary>
    public long Now { get; init; }

    /// <summary>Diagnostics written by <c>debug</c> and <c>stderr</c>.</summary>
    public StringBuilder Stderr { get; } = new();
}

/// <summary>
/// Evaluates a jq filter.
/// </summary>
/// <remarks>
/// <para>
/// Every filter is a stream transformer, so evaluation is modelled as
/// <see cref="IEnumerable{T}"/> throughout. That is not a stylistic choice: <c>limit</c>,
/// <c>first</c> and <c>break</c> all have to stop a generator part-way through, and lazy
/// enumeration is what makes <c>first(range(1e9))</c> return immediately instead of
/// building a billion values.
/// </para>
/// <para>
/// Path expressions are evaluated by a second traversal, <see cref="JqPaths"/>, which yields
/// the route to each value as well as the value. Assignment, <c>del</c> and <c>paths</c>
/// are all defined in terms of it, exactly as jq defines them.
/// </para>
/// </remarks>
internal sealed class JqEvaluator(JqRuntime runtime)
{
    private readonly JqRuntime _runtime = runtime;

    /// <summary>The runtime this evaluator was created for.</summary>
    public JqRuntime Runtime => _runtime;

    /// <summary>Evaluates <paramref name="node"/>, yielding every value it produces.</summary>
    public IEnumerable<JsonValue> Eval(JqNode node, JsonValue input, JqScope scope)
    {
        _runtime.Budget.ThrowIfExpired();

        switch (node)
        {
            case JqIdentity:
                return [input];

            case JqLiteral literal:
                return [literal.Value];

            case JqRecurseDefault:
                return Recurse(input);

            case JqPipe pipe:
                return Eval(pipe.Left, input, scope).SelectMany(value => Eval(pipe.Right, value, scope));

            case JqComma comma:
                return Eval(comma.Left, input, scope).Concat(Eval(comma.Right, input, scope));

            case JqIndex index:
                return EvalIndex(index, input, scope);

            case JqIterate iterate:
                return EvalIterate(iterate, input, scope);

            case JqSlice slice:
                return EvalSlice(slice, input, scope);

            case JqNegate negate:
                return Eval(negate.Operand, input, scope).Select(static value => value is JsonNumber number
                    ? (JsonValue)new JsonNumber(-number.Value)
                    : throw new JqException($"{value.TypeName} ({value}) cannot be negated"));

            case JqBinary binary:
                return EvalBinary(binary, input, scope);

            case JqIf conditional:
                return EvalIf(conditional, input, scope);

            case JqTry guarded:
                return EvalTry(guarded, input, scope);

            case JqStringInterp interpolation:
                return EvalInterpolation(interpolation, input, scope);

            case JqArrayCons array:
                return [new JsonArray(array.Body is null ? [] : Eval(array.Body, input, scope).ToList())];

            case JqObjectCons json:
                return EvalObject(json.Entries, 0, new JsonObject(), input, scope);

            case JqVariable variable:
                return [EvalVariable(variable.Name, scope)];

            case JqFormat format:
                return [new JsonString(JqFormats.Apply(format.Name, input))];

            case JqFuncDef definition:
                return EvalFuncDef(definition, input, scope);

            case JqCall call:
                return EvalCall(call, input, scope);

            case JqBind bind:
                return EvalBind(bind, input, scope);

            case JqReduce reduce:
                return EvalReduce(reduce, input, scope);

            case JqForeach loop:
                return EvalForeach(loop, input, scope);

            case JqAssign assign:
                return JqAssignment.Evaluate(this, assign, input, scope);

            case JqLabel label:
                return EvalLabel(label, input, scope);

            case JqBreak jump:
                throw new JqBreakException(jump.Name);

            default:
                throw new JqException("jq: error: unsupported filter");
        }
    }

    private JsonValue EvalVariable(string name, JqScope scope)
    {
        if (string.Equals(name, "ENV", StringComparison.Ordinal))
        {
            return _runtime.Environment;
        }

        if (string.Equals(name, "__loc__", StringComparison.Ordinal))
        {
            var location = new JsonObject();
            location.Set("file", new JsonString("<stdin>"));
            location.Set("line", new JsonNumber(1));
            return location;
        }

        return scope.Variable(name)
            ?? throw new JqException($"jq: error: ${name} is not defined");
    }

    private IEnumerable<JsonValue> EvalIndex(JqIndex node, JsonValue input, JqScope scope)
    {
        // The index expression sees the same input as the whole term, not the target's
        // value: `.a[.b]` looks `.b` up in `.`, which is what jq compiles.
        foreach (var target in Eval(node.Target, input, scope))
        {
            foreach (var key in Eval(node.Index, input, scope))
            {
                JsonValue value;

                try
                {
                    value = JqValues.Index(target, key);
                }
                catch (JqException) when (node.Optional)
                {
                    continue;
                }

                yield return value;
            }
        }
    }

    private IEnumerable<JsonValue> EvalIterate(JqIterate node, JsonValue input, JqScope scope)
    {
        foreach (var target in Eval(node.Target, input, scope))
        {
            if (target is JsonArray array)
            {
                foreach (var item in array.Items)
                {
                    yield return item;
                }

                continue;
            }

            if (target is JsonObject json)
            {
                foreach (var (_, value) in json.Entries())
                {
                    yield return value;
                }

                continue;
            }

            if (!node.Optional)
            {
                throw new JqException($"Cannot iterate over {Describe(target)}");
            }
        }
    }

    private IEnumerable<JsonValue> EvalSlice(JqSlice node, JsonValue input, JqScope scope)
    {
        var lower = node.From is null ? [JsonNull.Instance] : Eval(node.From, input, scope);

        foreach (var from in lower)
        {
            var upper = node.To is null ? [JsonNull.Instance] : Eval(node.To, input, scope);

            foreach (var to in upper)
            {
                foreach (var target in Eval(node.Target, input, scope))
                {
                    JsonValue value;

                    try
                    {
                        value = JqValues.Slice(target, from, to);
                    }
                    catch (JqException) when (node.Optional)
                    {
                        continue;
                    }

                    yield return value;
                }
            }
        }
    }

    private IEnumerable<JsonValue> EvalBinary(JqBinary node, JsonValue input, JqScope scope)
    {
        switch (node.Operator)
        {
            case "and":
            {
                foreach (var left in Eval(node.Left, input, scope))
                {
                    if (!left.IsTruthy)
                    {
                        yield return JsonBool.False;
                        continue;
                    }

                    foreach (var right in Eval(node.Right, input, scope))
                    {
                        yield return JsonValue.Of(right.IsTruthy);
                    }
                }

                yield break;
            }

            case "or":
            {
                foreach (var left in Eval(node.Left, input, scope))
                {
                    if (left.IsTruthy)
                    {
                        yield return JsonBool.True;
                        continue;
                    }

                    foreach (var right in Eval(node.Right, input, scope))
                    {
                        yield return JsonValue.Of(right.IsTruthy);
                    }
                }

                yield break;
            }

            case "//":
            {
                // `a // b` yields a's truthy outputs, and b's only when there were none.
                // Errors on the left count as "no output" rather than propagating.
                var produced = false;

                foreach (var value in Suppress(() => Eval(node.Left, input, scope)))
                {
                    if (!value.IsTruthy)
                    {
                        continue;
                    }

                    produced = true;
                    yield return value;
                }

                if (produced)
                {
                    yield break;
                }

                foreach (var value in Eval(node.Right, input, scope))
                {
                    yield return value;
                }

                yield break;
            }
        }

        foreach (var right in Eval(node.Right, input, scope))
        {
            foreach (var left in Eval(node.Left, input, scope))
            {
                yield return JqValues.Binary(node.Operator, left, right);
            }
        }
    }

    private IEnumerable<JsonValue> EvalIf(JqIf node, JsonValue input, JqScope scope)
    {
        foreach (var condition in Eval(node.Condition, input, scope))
        {
            if (condition.IsTruthy)
            {
                foreach (var value in Eval(node.Then, input, scope))
                {
                    yield return value;
                }

                continue;
            }

            // A missing `else` passes the input through, which is what makes
            // `if . then error else . end` a usable guard.
            if (node.Else is null)
            {
                yield return input;
                continue;
            }

            foreach (var value in Eval(node.Else, input, scope))
            {
                yield return value;
            }
        }
    }

    private IEnumerable<JsonValue> EvalTry(JqTry node, JsonValue input, JqScope scope)
    {
        // The enumerator is created inside the guard: a builtin such as `error` raises as
        // soon as it is called, before any value is pulled.
        IEnumerator<JsonValue>? enumerator = null;

        try
        {
            while (true)
            {
                JsonValue current;
                JqException? caught = null;

                try
                {
                    enumerator ??= Eval(node.Body, input, scope).GetEnumerator();

                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (JqException exception)
                {
                    caught = exception;
                    current = JsonNull.Instance;
                }

                if (caught is not null)
                {
                    if (node.Handler is null)
                    {
                        yield break;
                    }

                    foreach (var handled in Eval(node.Handler, caught.Payload, scope))
                    {
                        yield return handled;
                    }

                    yield break;
                }

                yield return current;
            }
        }
        finally
        {
            enumerator?.Dispose();
        }
    }

    private IEnumerable<JsonValue> EvalInterpolation(JqStringInterp node, JsonValue input, JqScope scope) =>
        Interpolate(node, 0, string.Empty, input, scope);

    private IEnumerable<JsonValue> Interpolate(
        JqStringInterp node,
        int index,
        string prefix,
        JsonValue input,
        JqScope scope)
    {
        if (index >= node.Parts.Count)
        {
            yield return new JsonString(prefix);
            yield break;
        }

        if (node.Parts[index] is string text)
        {
            foreach (var value in Interpolate(node, index + 1, prefix + text, input, scope))
            {
                yield return value;
            }

            yield break;
        }

        var part = (JqNode)node.Parts[index];

        foreach (var value in Eval(part, input, scope))
        {
            var rendered = node.Format is null ? JqValues.ToText(value) : JqFormats.Apply(node.Format, value);

            foreach (var result in Interpolate(node, index + 1, prefix + rendered, input, scope))
            {
                yield return result;
            }
        }
    }

    private IEnumerable<JsonValue> EvalObject(
        IReadOnlyList<JqObjectEntry> entries,
        int index,
        JsonObject accumulated,
        JsonValue input,
        JqScope scope)
    {
        if (index >= entries.Count)
        {
            yield return new JsonObject(accumulated);
            yield break;
        }

        foreach (var key in Eval(entries[index].Key, input, scope))
        {
            if (key is not JsonString name)
            {
                throw new JqException($"Object keys must be strings, not {key.TypeName} ({key})");
            }

            foreach (var value in Eval(entries[index].Value, input, scope))
            {
                var next = new JsonObject(accumulated);
                next.Set(name.Value, value);

                foreach (var result in EvalObject(entries, index + 1, next, input, scope))
                {
                    yield return result;
                }
            }
        }
    }

    private IEnumerable<JsonValue> EvalFuncDef(JqFuncDef node, JsonValue input, JqScope scope)
    {
        var closure = new JqClosure(node.Parameters, node.Body);
        var inner = scope.WithFunction(node.Name, node.Parameters.Count, closure);

        // The closure sees the scope it is bound in, so it can call itself.
        closure.Scope = inner;
        return Eval(node.Rest, input, inner);
    }

    private IEnumerable<JsonValue> EvalCall(JqCall node, JsonValue input, JqScope scope)
    {
        if (scope.Function(node.Name, node.Arguments.Count) is { } closure)
        {
            return Invoke(closure, node.Arguments, input, scope);
        }

        return JqBuiltins.Call(this, node, input, scope);
    }

    /// <summary>Calls a user-defined filter, binding its parameters.</summary>
    public IEnumerable<JsonValue> Invoke(
        JqClosure closure,
        IReadOnlyList<JqNode> arguments,
        JsonValue input,
        JqScope callerScope)
    {
        var body = closure.Scope;

        // A plain parameter is bound as a zero-argument filter closed over the caller's
        // scope, which is what makes `map(f)` see the caller's `.`-independent bindings.
        for (var i = 0; i < closure.Parameters.Count; i++)
        {
            var name = closure.Parameters[i];

            if (name.StartsWith('$'))
            {
                continue;
            }

            var argument = new JqClosure([], arguments[i]) { Scope = callerScope };
            body = body.WithFunction(name, 0, argument);
        }

        return BindValueParameters(closure, arguments, 0, body, input, callerScope);
    }

    /// <summary>
    /// Binds the <c>$</c>-prefixed parameters, one at a time.
    /// </summary>
    /// <remarks>
    /// <c>def f($a): ...</c> is sugar for <c>def f(a): a as $a | ...</c>, so an argument
    /// producing several values calls the body once per value — and several such parameters
    /// produce their cartesian product. Recursing gives that for free.
    /// </remarks>
    private IEnumerable<JsonValue> BindValueParameters(
        JqClosure closure,
        IReadOnlyList<JqNode> arguments,
        int index,
        JqScope scope,
        JsonValue input,
        JqScope callerScope)
    {
        while (index < closure.Parameters.Count && !closure.Parameters[index].StartsWith('$'))
        {
            index++;
        }

        if (index >= closure.Parameters.Count)
        {
            return Eval(closure.Body, input, scope);
        }

        var name = closure.Parameters[index][1..];

        return Eval(arguments[index], input, callerScope).SelectMany(value =>
        {
            var bound = scope.WithVariable(name, value);
            bound = bound.WithFunction(name, 0, new JqClosure([], new JqLiteral(value)) { Scope = bound });
            return BindValueParameters(closure, arguments, index + 1, bound, input, callerScope);
        });
    }

    private IEnumerable<JsonValue> EvalBind(JqBind node, JsonValue input, JqScope scope)
    {
        foreach (var value in Eval(node.Source, input, scope))
        {
            var bound = BindPattern(node.Patterns, value, scope);

            foreach (var result in Eval(node.Body, input, bound))
            {
                yield return result;
            }
        }
    }

    /// <summary>Applies the first pattern that matches, falling through the <c>?//</c> chain.</summary>
    private JqScope BindPattern(IReadOnlyList<JqPattern> patterns, JsonValue value, JqScope scope)
    {
        for (var i = 0; i < patterns.Count; i++)
        {
            try
            {
                return Destructure(patterns[i], value, scope);
            }
            catch (JqException) when (i + 1 < patterns.Count)
            {
                // Try the next alternative.
            }
        }

        return scope;
    }

    private JqScope Destructure(JqPattern pattern, JsonValue value, JqScope scope)
    {
        switch (pattern)
        {
            case JqVarPattern variable:
                return scope.WithVariable(variable.Name, value);

            case JqArrayPattern array:
            {
                for (var i = 0; i < array.Elements.Count; i++)
                {
                    scope = Destructure(array.Elements[i], JqValues.Index(value, new JsonNumber(i)), scope);
                }

                return scope;
            }

            case JqObjectPattern json:
            {
                foreach (var (keyNode, valuePattern) in json.Entries)
                {
                    foreach (var key in Eval(keyNode, value, scope))
                    {
                        scope = Destructure(valuePattern, JqValues.Index(value, key), scope);
                    }
                }

                return scope;
            }

            default:
                return scope;
        }
    }

    private IEnumerable<JsonValue> EvalReduce(JqReduce node, JsonValue input, JqScope scope)
    {
        foreach (var seed in Eval(node.Init, input, scope))
        {
            var accumulator = seed;

            foreach (var item in Eval(node.Source, input, scope))
            {
                var bound = Destructure(node.Pattern, item, scope);
                JsonValue? next = null;

                // The last output of the update wins, and no output at all resets the fold
                // to null — jq's rule, and the reason this is not a simple Aggregate.
                foreach (var candidate in Eval(node.Update, accumulator, bound))
                {
                    next = candidate;
                }

                accumulator = next ?? JsonNull.Instance;
            }

            yield return accumulator;
        }
    }

    private IEnumerable<JsonValue> EvalForeach(JqForeach node, JsonValue input, JqScope scope)
    {
        foreach (var seed in Eval(node.Init, input, scope))
        {
            var accumulator = seed;

            foreach (var item in Eval(node.Source, input, scope))
            {
                var bound = Destructure(node.Pattern, item, scope);

                foreach (var stepped in Eval(node.Update, accumulator, bound))
                {
                    accumulator = stepped;

                    if (node.Extract is null)
                    {
                        yield return stepped;
                        continue;
                    }

                    foreach (var extracted in Eval(node.Extract, stepped, bound))
                    {
                        yield return extracted;
                    }
                }
            }
        }
    }

    private IEnumerable<JsonValue> EvalLabel(JqLabel node, JsonValue input, JqScope scope)
    {
        using var enumerator = Eval(node.Body, input, scope).GetEnumerator();

        while (true)
        {
            JsonValue current;

            try
            {
                if (!enumerator.MoveNext())
                {
                    yield break;
                }

                current = enumerator.Current;
            }
            catch (JqBreakException jump) when (string.Equals(jump.Label, node.Name, StringComparison.Ordinal))
            {
                yield break;
            }

            yield return current;
        }
    }

    /// <summary>Runs a stream, dropping it at the first error rather than propagating.</summary>
    private static IEnumerable<JsonValue> Suppress(Func<IEnumerable<JsonValue>> source)
    {
        IEnumerator<JsonValue>? enumerator = null;

        try
        {
            while (true)
            {
                JsonValue current;

                try
                {
                    enumerator ??= source().GetEnumerator();

                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (JqException)
                {
                    yield break;
                }

                yield return current;
            }
        }
        finally
        {
            enumerator?.Dispose();
        }
    }

    /// <summary>The input and, depth-first, everything reachable inside it.</summary>
    public static IEnumerable<JsonValue> Recurse(JsonValue value)
    {
        yield return value;

        switch (value)
        {
            case JsonArray array:
            {
                foreach (var item in array.Items)
                {
                    foreach (var inner in Recurse(item))
                    {
                        yield return inner;
                    }
                }

                break;
            }

            case JsonObject json:
            {
                foreach (var (_, item) in json.Entries())
                {
                    foreach (var inner in Recurse(item))
                    {
                        yield return inner;
                    }
                }

                break;
            }
        }
    }

    /// <summary>Renders a value for an error message, as jq does: type then value.</summary>
    public static string Describe(JsonValue value)
    {
        var text = value.ToString();
        return $"{value.TypeName} ({(text.Length > 11 ? text[..10] + "..." : text)})";
    }
}
