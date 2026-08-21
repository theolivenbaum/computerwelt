namespace Computerwelt.Emulation.Bash.Builtins.Jq;

/// <summary>One route into a value, and the value found there.</summary>
/// <param name="Path">The steps taken, as jq's <c>path</c> reports them.</param>
/// <param name="Value">The value at the end of the route.</param>
internal readonly record struct JqPath(IReadOnlyList<JsonValue> Path, JsonValue Value);

/// <summary>
/// Evaluates a filter as a <i>path expression</i>.
/// </summary>
/// <remarks>
/// <para>
/// jq needs two readings of the same syntax. <c>.a.b</c> as an ordinary filter yields a
/// value; as a path expression it yields <c>["a","b"]</c>, and that second reading is what
/// makes <c>del</c>, <c>paths</c>, <c>|=</c> and every assignment operator work. Only the
/// navigational constructs have a path reading — <c>1</c> or <c>length</c> do not — which
/// is why an unsupported node raises rather than falling back.
/// </para>
/// <para>
/// User-defined filters are inlined here rather than called, so <c>select</c>,
/// <c>recurse</c> and <c>first</c> keep their path readings without being special-cased.
/// </para>
/// </remarks>
internal static class JqPaths
{
    /// <summary>Evaluates <paramref name="node"/> as a path expression rooted at <paramref name="input"/>.</summary>
    public static IEnumerable<JqPath> Evaluate(
        JqEvaluator evaluator,
        JqNode node,
        JqPath current,
        JsonValue input,
        JqScope scope)
    {
        evaluator.Runtime.Budget.ThrowIfExpired();

        switch (node)
        {
            case JqIdentity:
                return [current];

            case JqRecurseDefault:
                return Recurse(current);

            case JqPipe pipe:
                return Evaluate(evaluator, pipe.Left, current, input, scope)
                    .SelectMany(step => Evaluate(evaluator, pipe.Right, step, step.Value, scope));

            case JqComma comma:
                return Evaluate(evaluator, comma.Left, current, input, scope)
                    .Concat(Evaluate(evaluator, comma.Right, current, input, scope));

            case JqIndex index:
                return EvaluateIndex(evaluator, index, current, input, scope);

            case JqIterate iterate:
                return EvaluateIterate(evaluator, iterate, current, input, scope);

            case JqSlice slice:
                return EvaluateSlice(evaluator, slice, current, input, scope);

            case JqIf conditional:
                return EvaluateIf(evaluator, conditional, current, input, scope);

            case JqTry guarded:
                return Suppress(() => Evaluate(evaluator, guarded.Body, current, input, scope));

            case JqFuncDef definition:
            {
                var closure = new JqClosure(definition.Parameters, definition.Body);
                var inner = scope.WithFunction(definition.Name, definition.Parameters.Count, closure);
                closure.Scope = inner;
                return Evaluate(evaluator, definition.Rest, current, input, inner);
            }

            case JqBind bind:
                return EvaluateBind(evaluator, bind, current, input, scope);

            case JqReduce or JqForeach:
                throw new JqException("Invalid path expression");

            case JqCall call:
                return EvaluateCall(evaluator, call, current, input, scope);

            default:
                throw new JqException($"Invalid path expression with result {Describe(evaluator, node, input, scope)}");
        }
    }

    private static string Describe(JqEvaluator evaluator, JqNode node, JsonValue input, JqScope scope)
    {
        try
        {
            return evaluator.Eval(node, input, scope).FirstOrDefault()?.ToString() ?? "null";
        }
        catch (JqException)
        {
            return "null";
        }
    }

    private static IEnumerable<JqPath> EvaluateIndex(
        JqEvaluator evaluator,
        JqIndex node,
        JqPath current,
        JsonValue input,
        JqScope scope)
    {
        foreach (var step in Evaluate(evaluator, node.Target, current, input, scope))
        {
            foreach (var key in evaluator.Eval(node.Index, input, scope))
            {
                JsonValue value;

                try
                {
                    value = JqValues.Index(step.Value, key);
                }
                catch (JqException) when (node.Optional)
                {
                    continue;
                }

                yield return new JqPath([.. step.Path, key], value);
            }
        }
    }

    private static IEnumerable<JqPath> EvaluateIterate(
        JqEvaluator evaluator,
        JqIterate node,
        JqPath current,
        JsonValue input,
        JqScope scope)
    {
        foreach (var step in Evaluate(evaluator, node.Target, current, input, scope))
        {
            switch (step.Value)
            {
                case JsonArray array:
                {
                    for (var i = 0; i < array.Items.Count; i++)
                    {
                        yield return new JqPath([.. step.Path, new JsonNumber(i)], array.Items[i]);
                    }

                    break;
                }

                case JsonObject json:
                {
                    foreach (var (key, value) in json.Entries())
                    {
                        yield return new JqPath([.. step.Path, new JsonString(key)], value);
                    }

                    break;
                }

                case JsonNull when !node.Optional:
                    throw new JqException("Cannot iterate over null");

                default:
                    if (!node.Optional)
                    {
                        throw new JqException($"Cannot iterate over {JqEvaluator.Describe(step.Value)}");
                    }

                    break;
            }
        }
    }

    private static IEnumerable<JqPath> EvaluateSlice(
        JqEvaluator evaluator,
        JqSlice node,
        JqPath current,
        JsonValue input,
        JqScope scope)
    {
        var lower = node.From is null ? [JsonNull.Instance] : evaluator.Eval(node.From, input, scope);

        foreach (var from in lower)
        {
            var upper = node.To is null ? [JsonNull.Instance] : evaluator.Eval(node.To, input, scope);

            foreach (var to in upper)
            {
                foreach (var step in Evaluate(evaluator, node.Target, current, input, scope))
                {
                    // A slice's path step is an object, which is how jq distinguishes it
                    // from a plain index when the path is later applied.
                    var range = new JsonObject();
                    range.Set("start", from);
                    range.Set("end", to);

                    yield return new JqPath([.. step.Path, range], JqValues.Slice(step.Value, from, to));
                }
            }
        }
    }

    private static IEnumerable<JqPath> EvaluateIf(
        JqEvaluator evaluator,
        JqIf node,
        JqPath current,
        JsonValue input,
        JqScope scope)
    {
        foreach (var condition in evaluator.Eval(node.Condition, input, scope))
        {
            if (condition.IsTruthy)
            {
                foreach (var step in Evaluate(evaluator, node.Then, current, input, scope))
                {
                    yield return step;
                }

                continue;
            }

            if (node.Else is null)
            {
                yield return current;
                continue;
            }

            foreach (var step in Evaluate(evaluator, node.Else, current, input, scope))
            {
                yield return step;
            }
        }
    }

    private static IEnumerable<JqPath> EvaluateBind(
        JqEvaluator evaluator,
        JqBind node,
        JqPath current,
        JsonValue input,
        JqScope scope)
    {
        foreach (var value in evaluator.Eval(node.Source, input, scope))
        {
            if (node.Patterns is not [JqVarPattern variable])
            {
                throw new JqException("Invalid path expression");
            }

            foreach (var step in Evaluate(evaluator, node.Body, current, input, scope.WithVariable(variable.Name, value)))
            {
                yield return step;
            }
        }
    }

    private static IEnumerable<JqPath> EvaluateCall(
        JqEvaluator evaluator,
        JqCall node,
        JqPath current,
        JsonValue input,
        JqScope scope)
    {
        if (scope.Function(node.Name, node.Arguments.Count) is { } closure)
        {
            return InlineCall(evaluator, closure, node.Arguments, current, input, scope);
        }

        switch (node.Name)
        {
            case "empty" when node.Arguments.Count == 0:
                return [];

            case "error":
                return evaluator.Eval(node, input, scope).Select(static _ => default(JqPath));

            case "getpath" when node.Arguments.Count == 1:
                return evaluator.Eval(node.Arguments[0], input, scope).Select(argument =>
                {
                    if (argument is not JsonArray path)
                    {
                        throw new JqException("Path must be specified as an array");
                    }

                    return new JqPath(
                        [.. current.Path, .. path.Items],
                        JqValues.GetPath(current.Value, path.Items));
                });

            case "select" when node.Arguments.Count == 1:
                return evaluator.Eval(node.Arguments[0], current.Value, scope)
                    .Where(static condition => condition.IsTruthy)
                    .Select(_ => current);

            case "first" when node.Arguments.Count == 1:
                return Evaluate(evaluator, node.Arguments[0], current, input, scope).Take(1);

            case "last" when node.Arguments.Count == 1:
                return Evaluate(evaluator, node.Arguments[0], current, input, scope).TakeLast(1);

            case "limit" when node.Arguments.Count == 2:
                return evaluator.Eval(node.Arguments[0], input, scope).SelectMany(count =>
                    Evaluate(evaluator, node.Arguments[1], current, input, scope)
                        .Take(Math.Max((int)JqValues.ToNumber(count), 0)));

            default:
                throw new JqException($"Invalid path expression near attempt to call {node.Name}");
        }
    }

    private static IEnumerable<JqPath> InlineCall(
        JqEvaluator evaluator,
        JqClosure closure,
        IReadOnlyList<JqNode> arguments,
        JqPath current,
        JsonValue input,
        JqScope callerScope)
    {
        var body = closure.Scope;

        for (var i = 0; i < closure.Parameters.Count; i++)
        {
            var name = closure.Parameters[i];

            if (name.StartsWith('$'))
            {
                // A value parameter is bound eagerly; taking the first value keeps the path
                // reading single-valued, which is all any path-shaped filter needs.
                var value = evaluator.Eval(arguments[i], input, callerScope).FirstOrDefault() ?? JsonNull.Instance;
                body = body.WithVariable(name[1..], value);
                body = body.WithFunction(name[1..], 0, new JqClosure([], new JqLiteral(value)) { Scope = body });
                continue;
            }

            body = body.WithFunction(name, 0, new JqClosure([], arguments[i]) { Scope = callerScope });
        }

        return Evaluate(evaluator, closure.Body, current, input, body);
    }

    /// <summary>The path to the value itself and, depth-first, to everything inside it.</summary>
    private static IEnumerable<JqPath> Recurse(JqPath current)
    {
        yield return current;

        switch (current.Value)
        {
            case JsonArray array:
            {
                for (var i = 0; i < array.Items.Count; i++)
                {
                    foreach (var inner in Recurse(new JqPath([.. current.Path, new JsonNumber(i)], array.Items[i])))
                    {
                        yield return inner;
                    }
                }

                break;
            }

            case JsonObject json:
            {
                foreach (var (key, value) in json.Entries())
                {
                    foreach (var inner in Recurse(new JqPath([.. current.Path, new JsonString(key)], value)))
                    {
                        yield return inner;
                    }
                }

                break;
            }
        }
    }

    private static IEnumerable<JqPath> Suppress(Func<IEnumerable<JqPath>> source)
    {
        IEnumerator<JqPath>? enumerator = null;

        try
        {
            while (true)
            {
                JqPath current;

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
}
