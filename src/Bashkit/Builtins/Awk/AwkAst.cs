namespace Bashkit.Builtins.Awk;

/// <summary>A parsed AWK program: a list of pattern-action rules plus function definitions.</summary>
/// <param name="Rules">The rules, in source order.</param>
/// <param name="Functions">User-defined functions, by name.</param>
internal sealed record AwkProgram(
    IReadOnlyList<AwkRule> Rules,
    IReadOnlyDictionary<string, AwkFunction> Functions);

/// <summary>What decides whether a rule's action runs.</summary>
internal enum AwkPatternKind
{
    /// <summary>No pattern: the action runs for every record.</summary>
    Always,

    /// <summary><c>BEGIN</c>, run once before any input.</summary>
    Begin,

    /// <summary><c>END</c>, run once after all input.</summary>
    End,

    /// <summary>An expression, true when it evaluates truthy.</summary>
    Expression,

    /// <summary>A range, active from the first pattern matching until the second does.</summary>
    Range,
}

/// <summary>One pattern-action rule.</summary>
internal sealed class AwkRule
{
    /// <summary>What selects this rule.</summary>
    public required AwkPatternKind Kind { get; init; }

    /// <summary>The selecting expression, or the range start.</summary>
    public AwkExpression? Pattern { get; init; }

    /// <summary>The range end, for <see cref="AwkPatternKind.Range"/>.</summary>
    public AwkExpression? RangeEnd { get; init; }

    /// <summary>The action body, or null for the implicit <c>{ print }</c>.</summary>
    public AwkStatement? Action { get; init; }

    /// <summary>Whether a range rule is currently inside its range.</summary>
    public bool RangeActive { get; set; }
}

/// <summary>A user-defined function.</summary>
/// <param name="Name">The function's name.</param>
/// <param name="Parameters">Its parameter names; extras act as locals, as AWK requires.</param>
/// <param name="Body">Its body.</param>
internal sealed record AwkFunction(string Name, IReadOnlyList<string> Parameters, AwkStatement Body);

// ---- statements ----

/// <summary>Base of every AWK statement.</summary>
internal abstract record AwkStatement;

/// <summary>A braced list of statements.</summary>
/// <param name="Statements">The contained statements.</param>
internal sealed record AwkBlock(IReadOnlyList<AwkStatement> Statements) : AwkStatement;

/// <summary>An expression evaluated for its effect.</summary>
/// <param name="Value">The expression.</param>
internal sealed record AwkExpressionStatement(AwkExpression Value) : AwkStatement;

/// <summary><c>print</c>.</summary>
/// <param name="Arguments">The values to print; empty means <c>$0</c>.</param>
/// <param name="Redirect">An output redirection, if any.</param>
internal sealed record AwkPrint(IReadOnlyList<AwkExpression> Arguments, AwkRedirect? Redirect) : AwkStatement;

/// <summary><c>printf</c>.</summary>
/// <param name="Arguments">The format string followed by its operands.</param>
/// <param name="Redirect">An output redirection, if any.</param>
internal sealed record AwkPrintf(IReadOnlyList<AwkExpression> Arguments, AwkRedirect? Redirect) : AwkStatement;

/// <summary>An output redirection attached to <c>print</c> or <c>printf</c>.</summary>
/// <param name="Kind">Whether it truncates, appends or pipes.</param>
/// <param name="Target">The filename or command.</param>
internal sealed record AwkRedirect(AwkRedirectKind Kind, AwkExpression Target);

/// <summary>The kinds of output redirection AWK supports.</summary>
internal enum AwkRedirectKind
{
    /// <summary><c>&gt;</c></summary>
    Truncate,

    /// <summary><c>&gt;&gt;</c></summary>
    Append,

    /// <summary><c>|</c></summary>
    Pipe,
}

/// <summary><c>if</c>.</summary>
/// <param name="Condition">The condition.</param>
/// <param name="Then">The consequent.</param>
/// <param name="Else">The alternative, if any.</param>
internal sealed record AwkIf(AwkExpression Condition, AwkStatement Then, AwkStatement? Else) : AwkStatement;

/// <summary><c>while</c>.</summary>
/// <param name="Condition">The condition.</param>
/// <param name="Body">The loop body.</param>
internal sealed record AwkWhile(AwkExpression Condition, AwkStatement Body) : AwkStatement;

/// <summary><c>do ... while</c>.</summary>
/// <param name="Body">The loop body, which always runs once.</param>
/// <param name="Condition">The condition tested after each iteration.</param>
internal sealed record AwkDoWhile(AwkStatement Body, AwkExpression Condition) : AwkStatement;

/// <summary>C-style <c>for</c>.</summary>
/// <param name="Init">The initializer, if any.</param>
/// <param name="Condition">The condition; absent means always true.</param>
/// <param name="Update">The update, if any.</param>
/// <param name="Body">The loop body.</param>
internal sealed record AwkFor(AwkStatement? Init, AwkExpression? Condition, AwkStatement? Update, AwkStatement Body)
    : AwkStatement;

/// <summary><c>for (key in array)</c>.</summary>
/// <param name="Variable">The name bound to each key.</param>
/// <param name="ArrayName">The array iterated.</param>
/// <param name="Body">The loop body.</param>
internal sealed record AwkForIn(string Variable, string ArrayName, AwkStatement Body) : AwkStatement;

/// <summary><c>next</c> — stop processing this record.</summary>
internal sealed record AwkNext : AwkStatement;

/// <summary><c>nextfile</c> — stop processing this input file.</summary>
internal sealed record AwkNextFile : AwkStatement;

/// <summary><c>exit</c>.</summary>
/// <param name="Code">The exit status expression, if given.</param>
internal sealed record AwkExit(AwkExpression? Code) : AwkStatement;

/// <summary><c>break</c>.</summary>
internal sealed record AwkBreak : AwkStatement;

/// <summary><c>continue</c>.</summary>
internal sealed record AwkContinue : AwkStatement;

/// <summary><c>return</c>.</summary>
/// <param name="Value">The returned expression, if any.</param>
internal sealed record AwkReturn(AwkExpression? Value) : AwkStatement;

/// <summary><c>delete</c>.</summary>
/// <param name="ArrayName">The array.</param>
/// <param name="Subscript">The element to remove, or null to clear the whole array.</param>
internal sealed record AwkDelete(string ArrayName, IReadOnlyList<AwkExpression>? Subscript) : AwkStatement;

/// <summary><c>getline</c> in statement position.</summary>
/// <param name="Target">Where to store the record; null means <c>$0</c>.</param>
/// <param name="Source">The file or command read from; null means the main input.</param>
/// <param name="FromPipe">True when <paramref name="Source"/> is a command.</param>
internal sealed record AwkGetlineStatement(AwkExpression? Target, AwkExpression? Source, bool FromPipe) : AwkStatement;

/// <summary>An empty statement, from a stray semicolon.</summary>
internal sealed record AwkEmpty : AwkStatement;

// ---- expressions ----

/// <summary>Base of every AWK expression.</summary>
internal abstract record AwkExpression;

/// <summary>A numeric literal.</summary>
/// <param name="Value">The value.</param>
internal sealed record AwkNumber(double Value) : AwkExpression;

/// <summary>A string literal.</summary>
/// <param name="Value">The already-unescaped text.</param>
internal sealed record AwkString(string Value) : AwkExpression;

/// <summary>A regular-expression literal, <c>/re/</c>.</summary>
/// <param name="Pattern">The pattern source.</param>
internal sealed record AwkRegex(string Pattern) : AwkExpression;

/// <summary>A variable reference.</summary>
/// <param name="Name">The variable's name.</param>
internal sealed record AwkVariable(string Name) : AwkExpression;

/// <summary>A field reference, <c>$n</c>.</summary>
/// <param name="Index">The field number expression.</param>
internal sealed record AwkField(AwkExpression Index) : AwkExpression;

/// <summary>An array element, <c>a[i]</c> or <c>a[i, j]</c>.</summary>
/// <param name="Name">The array's name.</param>
/// <param name="Subscript">The subscript expressions, joined by <c>SUBSEP</c>.</param>
internal sealed record AwkIndex(string Name, IReadOnlyList<AwkExpression> Subscript) : AwkExpression;

/// <summary>A binary operation.</summary>
/// <param name="Operator">The operator token.</param>
/// <param name="Left">The left operand.</param>
/// <param name="Right">The right operand.</param>
internal sealed record AwkBinary(string Operator, AwkExpression Left, AwkExpression Right) : AwkExpression;

/// <summary>A unary operation.</summary>
/// <param name="Operator">Either <c>-</c>, <c>+</c> or <c>!</c>.</param>
/// <param name="Operand">The operand.</param>
internal sealed record AwkUnary(string Operator, AwkExpression Operand) : AwkExpression;

/// <summary>String concatenation, which in AWK has no operator at all.</summary>
/// <param name="Left">The left operand.</param>
/// <param name="Right">The right operand.</param>
internal sealed record AwkConcat(AwkExpression Left, AwkExpression Right) : AwkExpression;

/// <summary>An assignment, possibly compound.</summary>
/// <param name="Target">The assignment target.</param>
/// <param name="Operator">The operator, <c>=</c> or a compound form.</param>
/// <param name="Value">The assigned value.</param>
internal sealed record AwkAssign(AwkExpression Target, string Operator, AwkExpression Value) : AwkExpression;

/// <summary>The ternary conditional.</summary>
/// <param name="Condition">The condition.</param>
/// <param name="Then">The value when true.</param>
/// <param name="Else">The value when false.</param>
internal sealed record AwkConditional(AwkExpression Condition, AwkExpression Then, AwkExpression Else) : AwkExpression;

/// <summary>A regex match, <c>~</c> or <c>!~</c>.</summary>
/// <param name="Subject">The left operand.</param>
/// <param name="Pattern">The pattern operand.</param>
/// <param name="Negated">True for <c>!~</c>.</param>
internal sealed record AwkMatch(AwkExpression Subject, AwkExpression Pattern, bool Negated) : AwkExpression;

/// <summary>Membership, <c>(i) in a</c>.</summary>
/// <param name="Subscript">The subscript expressions.</param>
/// <param name="ArrayName">The array tested.</param>
internal sealed record AwkIn(IReadOnlyList<AwkExpression> Subscript, string ArrayName) : AwkExpression;

/// <summary>Pre- or post-increment and decrement.</summary>
/// <param name="Target">The target.</param>
/// <param name="Delta">Either 1 or -1.</param>
/// <param name="Prefix">True when the new value is the expression's result.</param>
internal sealed record AwkIncrement(AwkExpression Target, int Delta, bool Prefix) : AwkExpression;

/// <summary>A call to a builtin or user-defined function.</summary>
/// <param name="Name">The function's name.</param>
/// <param name="Arguments">The argument expressions.</param>
internal sealed record AwkCall(string Name, IReadOnlyList<AwkExpression> Arguments) : AwkExpression;

/// <summary><c>getline</c> used as an expression, which yields 1, 0 or -1.</summary>
/// <param name="Target">Where to store the record; null means <c>$0</c>.</param>
/// <param name="Source">The file or command read from; null means the main input.</param>
/// <param name="FromPipe">True when <paramref name="Source"/> is a command.</param>
internal sealed record AwkGetline(AwkExpression? Target, AwkExpression? Source, bool FromPipe) : AwkExpression;

/// <summary>A parenthesized group, kept so that <c>(a, b) in arr</c> stays distinguishable.</summary>
/// <param name="Items">The grouped expressions.</param>
internal sealed record AwkGroup(IReadOnlyList<AwkExpression> Items) : AwkExpression;
