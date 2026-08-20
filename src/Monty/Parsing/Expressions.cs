namespace Monty.Parsing;

/// <summary>Base of every expression.</summary>
public abstract record Expression : PyNode;

/// <summary>A literal constant: number, string, bytes, <c>True</c>, <c>False</c>, <c>None</c>, <c>...</c>.</summary>
/// <param name="Value">The literal value, already decoded.</param>
public sealed record Literal(object? Value) : Expression
{
    /// <summary>
    /// True when the source spelled a <c>u</c> prefix on a string literal.
    /// </summary>
    /// <remarks>
    /// The prefix means nothing at run time — every string is Unicode — but it survives
    /// into the canonical text of a stringized annotation, so it has to survive parsing.
    /// </remarks>
    public bool HasUnicodePrefix { get; init; }
}

/// <summary>A name reference.</summary>
/// <param name="Id">The identifier.</param>
public sealed record Name(string Id) : Expression;

/// <summary>A binary operation.</summary>
/// <param name="Operator">The operator token.</param>
/// <param name="Left">The left operand.</param>
/// <param name="Right">The right operand.</param>
public sealed record BinaryOp(string Operator, Expression Left, Expression Right) : Expression;

/// <summary>A unary operation: <c>-</c>, <c>+</c>, <c>~</c> or <c>not</c>.</summary>
/// <param name="Operator">The operator token.</param>
/// <param name="Operand">The operand.</param>
public sealed record UnaryOp(string Operator, Expression Operand) : Expression;

/// <summary>
/// A comparison chain such as <c>a &lt; b &lt;= c</c>.
/// </summary>
/// <remarks>
/// Chains are one node, not nested binaries, because Python evaluates each operand once
/// and short-circuits: <c>a &lt; f() &lt; b</c> calls <c>f</c> exactly once.
/// </remarks>
/// <param name="Left">The first operand.</param>
/// <param name="Operators">The operators, in order.</param>
/// <param name="Comparators">The remaining operands, in order.</param>
public sealed record Compare(Expression Left, IReadOnlyList<string> Operators, IReadOnlyList<Expression> Comparators) : Expression;

/// <summary>A short-circuiting <c>and</c> / <c>or</c> chain.</summary>
/// <param name="Operator">Either <c>and</c> or <c>or</c>.</param>
/// <param name="Values">The operands, in order.</param>
public sealed record BoolOp(string Operator, IReadOnlyList<Expression> Values) : Expression;

/// <summary>A call.</summary>
/// <param name="Function">The callee.</param>
/// <param name="Arguments">Positional arguments; a <see cref="Starred"/> element unpacks.</param>
/// <param name="Keywords">Keyword arguments; a null name means <c>**</c> unpacking.</param>
public sealed record Call(Expression Function, IReadOnlyList<Expression> Arguments, IReadOnlyList<KeywordArgument> Keywords) : Expression;

/// <summary>One keyword argument.</summary>
/// <param name="Name">The keyword, or <see langword="null"/> for <c>**mapping</c>.</param>
/// <param name="Value">The argument value.</param>
public readonly record struct KeywordArgument(string? Name, Expression Value);

/// <summary>Attribute access, <c>obj.name</c>.</summary>
/// <param name="Value">The object.</param>
/// <param name="AttributeName">The attribute.</param>
public sealed record Attribute(Expression Value, string AttributeName) : Expression;

/// <summary>Subscription, <c>obj[index]</c>.</summary>
/// <param name="Value">The object.</param>
/// <param name="Index">The subscript, which may be a <see cref="Slice"/>.</param>
public sealed record Subscript(Expression Value, Expression Index) : Expression;

/// <summary>A slice, <c>a:b:c</c>.</summary>
/// <param name="Lower">The start, if given.</param>
/// <param name="Upper">The stop, if given.</param>
/// <param name="Step">The step, if given.</param>
public sealed record Slice(Expression? Lower, Expression? Upper, Expression? Step) : Expression;

/// <summary>A list display.</summary>
/// <param name="Elements">The elements.</param>
public sealed record ListExpr(IReadOnlyList<Expression> Elements) : Expression;

/// <summary>A tuple display.</summary>
/// <param name="Elements">The elements.</param>
public sealed record TupleExpr(IReadOnlyList<Expression> Elements) : Expression;

/// <summary>A set display.</summary>
/// <param name="Elements">The elements.</param>
public sealed record SetExpr(IReadOnlyList<Expression> Elements) : Expression;

/// <summary>A dict display.</summary>
/// <param name="Keys">The keys; a null key means <c>**mapping</c> unpacking of the matching value.</param>
/// <param name="Values">The values.</param>
public sealed record DictExpr(IReadOnlyList<Expression?> Keys, IReadOnlyList<Expression> Values) : Expression;

/// <summary>A starred expression, <c>*x</c>, used for unpacking.</summary>
/// <param name="Value">The unpacked expression.</param>
public sealed record Starred(Expression Value) : Expression;

/// <summary>A double-starred expression, <c>**x</c>.</summary>
/// <param name="Value">The unpacked mapping.</param>
public sealed record DoubleStarred(Expression Value) : Expression;

/// <summary>A conditional expression, <c>a if test else b</c>.</summary>
/// <param name="Test">The condition.</param>
/// <param name="Body">The value when the condition is true.</param>
/// <param name="OrElse">The value when it is false.</param>
public sealed record Conditional(Expression Test, Expression Body, Expression OrElse) : Expression;

/// <summary>A lambda.</summary>
/// <param name="Parameters">Its parameter list.</param>
/// <param name="Body">Its single expression body.</param>
public sealed record Lambda(ParameterList Parameters, Expression Body) : Expression;

/// <summary>The kinds of comprehension, which differ only in what they build.</summary>
public enum ComprehensionKind
{
    /// <summary><c>[x for x in y]</c></summary>
    List,

    /// <summary><c>{x for x in y}</c></summary>
    Set,

    /// <summary><c>{k: v for k, v in y}</c></summary>
    Dictionary,

    /// <summary><c>(x for x in y)</c></summary>
    Generator,
}

/// <summary>A comprehension.</summary>
/// <param name="Kind">What it builds.</param>
/// <param name="Element">The element expression, or the value for a dict comprehension.</param>
/// <param name="Key">The key expression, for a dict comprehension only.</param>
/// <param name="Clauses">The <c>for</c> clauses and their conditions, outermost first.</param>
public sealed record Comprehension(
    ComprehensionKind Kind,
    Expression Element,
    Expression? Key,
    IReadOnlyList<ComprehensionClause> Clauses) : Expression;

/// <summary>One <c>for</c> clause of a comprehension.</summary>
/// <param name="Target">The bound target.</param>
/// <param name="Iterable">The iterated expression.</param>
/// <param name="Conditions">The <c>if</c> conditions attached to this clause.</param>
/// <param name="IsAsync">True for <c>async for</c>.</param>
public sealed record ComprehensionClause(
    Expression Target,
    Expression Iterable,
    IReadOnlyList<Expression> Conditions,
    bool IsAsync);

/// <summary>An f-string.</summary>
/// <param name="Parts">Alternating literal and interpolated parts, in order.</param>
public sealed record FormattedString(IReadOnlyList<FormatPart> Parts) : Expression;

/// <summary>One part of an f-string.</summary>
/// <param name="Literal">
/// Literal text emitted before this part. For a pure literal part <see cref="Value"/> is
/// null; for the <c>f'{x=}'</c> debug form both are set, since it prints the source text
/// and then the value.
/// </param>
/// <param name="Value">The interpolated expression, when there is one.</param>
/// <param name="Conversion">The <c>!r</c>, <c>!s</c> or <c>!a</c> conversion, or <c>\0</c>.</param>
/// <param name="FormatSpec">The format spec after <c>:</c>, if given.</param>
public readonly record struct FormatPart(string? Literal, Expression? Value, char Conversion, Expression? FormatSpec)
{
    /// <summary>Literal text emitted before the interpolated value.</summary>
    public string? Literal { get; init; } = Literal;
}

/// <summary>An assignment expression, <c>name := value</c>.</summary>
/// <param name="Target">The assigned name.</param>
/// <param name="Value">The value, which is also the expression's result.</param>
public sealed record NamedExpr(Name Target, Expression Value) : Expression;

/// <summary><c>yield</c> or <c>yield from</c>.</summary>
/// <param name="Value">The yielded expression, if any.</param>
/// <param name="IsDelegating">True for <c>yield from</c>.</param>
public sealed record Yield(Expression? Value, bool IsDelegating) : Expression;

/// <summary><c>await</c>.</summary>
/// <param name="Value">The awaited expression.</param>
public sealed record Await(Expression Value) : Expression;
