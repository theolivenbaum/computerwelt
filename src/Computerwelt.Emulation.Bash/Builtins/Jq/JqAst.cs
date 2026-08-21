namespace Computerwelt.Emulation.Bash.Builtins.Jq;

/// <summary>
/// A jq filter.
/// </summary>
/// <remarks>
/// Every filter is a function from one input value to a <i>stream</i> of output values, so
/// there is no distinction here between an expression and a generator: <c>.[]</c> and
/// <c>1</c> are the same kind of node, differing only in how many values they produce.
/// </remarks>
internal abstract record JqNode;

/// <summary><c>.</c> — the input, unchanged.</summary>
internal sealed record JqIdentity : JqNode;

/// <summary><c>..</c> — the input and every value reachable inside it.</summary>
internal sealed record JqRecurseDefault : JqNode;

/// <summary>A literal value.</summary>
/// <param name="Value">The value.</param>
internal sealed record JqLiteral(JsonValue Value) : JqNode;

/// <summary>An interpolated string, optionally passed through a <c>@format</c>.</summary>
/// <param name="Parts">Literal text as strings and interpolations as nodes, in order.</param>
/// <param name="Format">The <c>@</c> format applied to each interpolation, if any.</param>
internal sealed record JqStringInterp(IReadOnlyList<object> Parts, string? Format) : JqNode;

/// <summary><c>a | b</c>.</summary>
/// <param name="Left">The producer.</param>
/// <param name="Right">The consumer, run once per value the producer yields.</param>
internal sealed record JqPipe(JqNode Left, JqNode Right) : JqNode;

/// <summary><c>a, b</c> — the two streams, one after the other.</summary>
/// <param name="Left">The first stream.</param>
/// <param name="Right">The second stream.</param>
internal sealed record JqComma(JqNode Left, JqNode Right) : JqNode;

/// <summary><c>t[i]</c> and <c>t.name</c>.</summary>
/// <param name="Target">What is indexed.</param>
/// <param name="Index">The key or position.</param>
/// <param name="Optional">True for <c>?</c>, which swallows a type error.</param>
internal sealed record JqIndex(JqNode Target, JqNode Index, bool Optional) : JqNode;

/// <summary><c>t[]</c> — every element or value.</summary>
/// <param name="Target">What is iterated.</param>
/// <param name="Optional">True for <c>?</c>.</param>
internal sealed record JqIterate(JqNode Target, bool Optional) : JqNode;

/// <summary><c>t[a:b]</c>.</summary>
/// <param name="Target">What is sliced.</param>
/// <param name="From">The start, or null for the beginning.</param>
/// <param name="To">The end, or null for the end.</param>
/// <param name="Optional">True for <c>?</c>.</param>
internal sealed record JqSlice(JqNode Target, JqNode? From, JqNode? To, bool Optional) : JqNode;

/// <summary>A unary or binary operator.</summary>
/// <param name="Operator">The operator's spelling.</param>
/// <param name="Left">The left operand.</param>
/// <param name="Right">The right operand.</param>
internal sealed record JqBinary(string Operator, JqNode Left, JqNode Right) : JqNode;

/// <summary>Arithmetic negation.</summary>
/// <param name="Operand">The operand.</param>
internal sealed record JqNegate(JqNode Operand) : JqNode;

/// <summary><c>if ... then ... elif ... else ... end</c>.</summary>
/// <param name="Condition">The condition.</param>
/// <param name="Then">The consequent.</param>
/// <param name="Else">The alternative; absent means the input passes through.</param>
internal sealed record JqIf(JqNode Condition, JqNode Then, JqNode? Else) : JqNode;

/// <summary><c>try b catch h</c>, and the postfix <c>b?</c>.</summary>
/// <param name="Body">The guarded filter.</param>
/// <param name="Handler">The handler, which receives the error message; absent discards it.</param>
internal sealed record JqTry(JqNode Body, JqNode? Handler) : JqNode;

/// <summary><c>reduce source as $x (init; update)</c>.</summary>
/// <param name="Source">The generator whose values drive the fold.</param>
/// <param name="Pattern">The binding pattern.</param>
/// <param name="Init">The initial accumulator.</param>
/// <param name="Update">The step, whose input is the accumulator.</param>
internal sealed record JqReduce(JqNode Source, JqPattern Pattern, JqNode Init, JqNode Update) : JqNode;

/// <summary><c>foreach source as $x (init; update; extract)</c>.</summary>
/// <param name="Source">The generator.</param>
/// <param name="Pattern">The binding pattern.</param>
/// <param name="Init">The initial accumulator.</param>
/// <param name="Update">The step.</param>
/// <param name="Extract">What to emit per step; absent emits the accumulator.</param>
internal sealed record JqForeach(
    JqNode Source,
    JqPattern Pattern,
    JqNode Init,
    JqNode Update,
    JqNode? Extract) : JqNode;

/// <summary><c>source as $x | body</c>.</summary>
/// <param name="Source">The generator being bound.</param>
/// <param name="Patterns">The alternative patterns, separated by <c>?//</c>.</param>
/// <param name="Body">The filter evaluated once per bound value.</param>
internal sealed record JqBind(JqNode Source, IReadOnlyList<JqPattern> Patterns, JqNode Body) : JqNode;

/// <summary>A variable reference, <c>$name</c>.</summary>
/// <param name="Name">The name, without the sigil.</param>
internal sealed record JqVariable(string Name) : JqNode;

/// <summary>A call to a builtin or a user-defined filter.</summary>
/// <param name="Name">The filter's name.</param>
/// <param name="Arguments">The argument filters, unevaluated.</param>
internal sealed record JqCall(string Name, IReadOnlyList<JqNode> Arguments) : JqNode;

/// <summary><c>def name(params): body; rest</c>.</summary>
/// <param name="Name">The defined name.</param>
/// <param name="Parameters">Its parameters; a <c>$</c> prefix means the value is bound eagerly.</param>
/// <param name="Body">The definition.</param>
/// <param name="Rest">The filter the definition is in scope for.</param>
internal sealed record JqFuncDef(
    string Name,
    IReadOnlyList<string> Parameters,
    JqNode Body,
    JqNode Rest) : JqNode;

/// <summary><c>[ ... ]</c>.</summary>
/// <param name="Body">The filter whose whole stream is collected, or null for <c>[]</c>.</param>
internal sealed record JqArrayCons(JqNode? Body) : JqNode;

/// <summary><c>{ ... }</c>.</summary>
/// <param name="Entries">The key/value filter pairs.</param>
internal sealed record JqObjectCons(IReadOnlyList<JqObjectEntry> Entries) : JqNode;

/// <summary>One entry of an object construction.</summary>
/// <param name="Key">The filter producing the key.</param>
/// <param name="Value">The filter producing the value.</param>
internal sealed record JqObjectEntry(JqNode Key, JqNode Value);

/// <summary>An assignment, in any of jq's six forms.</summary>
/// <param name="Operator">The operator, from <c>=</c> to <c>//=</c>.</param>
/// <param name="Target">The path expression being written to.</param>
/// <param name="Value">The filter producing the new value.</param>
internal sealed record JqAssign(string Operator, JqNode Target, JqNode Value) : JqNode;

/// <summary>A bare <c>@format</c>, which converts its input to a string.</summary>
/// <param name="Name">The format's name, without the sigil.</param>
internal sealed record JqFormat(string Name) : JqNode;

/// <summary><c>label $out | body</c>.</summary>
/// <param name="Name">The label's name.</param>
/// <param name="Body">The filter it wraps.</param>
internal sealed record JqLabel(string Name, JqNode Body) : JqNode;

/// <summary><c>break $out</c>.</summary>
/// <param name="Name">The label to unwind to.</param>
internal sealed record JqBreak(string Name) : JqNode;

/// <summary>A destructuring pattern on the left of <c>as</c>.</summary>
internal abstract record JqPattern;

/// <summary>A plain <c>$name</c> pattern.</summary>
/// <param name="Name">The bound name.</param>
internal sealed record JqVarPattern(string Name) : JqPattern;

/// <summary><c>[$a, $b]</c>.</summary>
/// <param name="Elements">The element patterns, matched positionally.</param>
internal sealed record JqArrayPattern(IReadOnlyList<JqPattern> Elements) : JqPattern;

/// <summary><c>{a: $x, $y}</c>.</summary>
/// <param name="Entries">The key filters and the patterns their values are matched against.</param>
internal sealed record JqObjectPattern(IReadOnlyList<(JqNode Key, JqPattern Value)> Entries) : JqPattern;
