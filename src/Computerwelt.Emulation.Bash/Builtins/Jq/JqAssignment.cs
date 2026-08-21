namespace Computerwelt.Emulation.Bash.Builtins.Jq;

/// <summary>
/// jq's six assignment operators.
/// </summary>
/// <remarks>
/// <para>
/// All of them work the same way: the left side is evaluated as a <i>path</i> expression,
/// and each resulting route is rewritten in a copy of the input. That is why <c>.a = 1</c>
/// and <c>.[] = 1</c> are the same construct — the second simply produces more paths.
/// </para>
/// <para>
/// The one difference between <c>=</c> and the rest is where the right side's input comes
/// from. <c>.a = .b</c> evaluates <c>.b</c> against the whole input, while <c>.a |= f</c>
/// evaluates <c>f</c> against the value already at <c>.a</c>.
/// </para>
/// </remarks>
internal static class JqAssignment
{
    /// <summary>Evaluates an assignment.</summary>
    public static IEnumerable<JsonValue> Evaluate(
        JqEvaluator evaluator,
        JqAssign node,
        JsonValue input,
        JqScope scope)
    {
        if (node.Operator == "|=")
        {
            return [Update(evaluator, node.Target, node.Value, input, scope)];
        }

        return evaluator.Eval(node.Value, input, scope).Select(value =>
            Apply(evaluator, node.Operator, node.Target, value, input, scope));
    }

    private static JsonValue Apply(
        JqEvaluator evaluator,
        string op,
        JqNode target,
        JsonValue value,
        JsonValue input,
        JqScope scope)
    {
        var result = input;

        foreach (var path in Paths(evaluator, target, input, scope))
        {
            var replacement = op switch
            {
                "=" => value,
                "//=" => JqValues.GetPath(result, path) is { IsTruthy: true } kept ? kept : value,
                _ => JqValues.Binary(op[..^1], JqValues.GetPath(result, path), value),
            };

            result = JqValues.SetPath(result, path, 0, replacement);
        }

        return result;
    }

    private static JsonValue Update(
        JqEvaluator evaluator,
        JqNode target,
        JqNode update,
        JsonValue input,
        JqScope scope)
    {
        var result = input;

        // Paths are re-read against the evolving result, because an update that deletes an
        // element shifts every later index.
        foreach (var path in Paths(evaluator, target, input, scope))
        {
            var current = JqValues.GetPath(result, path);
            var replacement = evaluator.Eval(update, current, scope).Take(1).ToList();

            // An update producing nothing deletes the path, which is how `del`-like
            // filters such as `.a |= empty` behave.
            result = replacement.Count == 0
                ? JqValues.DeletePath(result, path, 0)
                : JqValues.SetPath(result, path, 0, replacement[0]);
        }

        return result;
    }

    private static List<IReadOnlyList<JsonValue>> Paths(
        JqEvaluator evaluator,
        JqNode target,
        JsonValue input,
        JqScope scope) =>
        JqPaths.Evaluate(evaluator, target, new JqPath([], input), input, scope)
            .Select(static step => step.Path)
            .ToList();
}
