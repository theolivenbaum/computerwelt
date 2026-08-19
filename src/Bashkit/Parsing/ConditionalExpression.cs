namespace Bashkit.Parsing;

/// <summary>
/// A condition inside <c>[[ ... ]]</c>.
/// </summary>
/// <remarks>
/// <c>[[ ]]</c> is a keyword, not the <c>test</c> builtin, so its operands are not
/// word-split or globbed and its <c>==</c>/<c>!=</c> right-hand sides are patterns rather
/// than literals. Parsing it into a tree — instead of handing a flat argv to <c>test</c> —
/// is what makes those differences expressible.
/// </remarks>
public abstract record ConditionalExpression
{
    /// <summary>A unary test such as <c>-f file</c> or <c>-z string</c>.</summary>
    /// <param name="Operator">The operator including its leading dash.</param>
    /// <param name="Operand">The operand word.</param>
    public sealed record Unary(string Operator, Word Operand) : ConditionalExpression;

    /// <summary>A binary test such as <c>a == b</c> or <c>x -lt y</c>.</summary>
    /// <param name="Operator">The operator text.</param>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand, treated as a pattern for <c>==</c>, <c>!=</c> and <c>=~</c>.</param>
    public sealed record Binary(string Operator, Word Left, Word Right) : ConditionalExpression;

    /// <summary>A bare word, true when it expands to a non-empty string.</summary>
    /// <param name="Operand">The word.</param>
    public sealed record Value(Word Operand) : ConditionalExpression;

    /// <summary>Logical negation.</summary>
    /// <param name="Operand">The negated expression.</param>
    public sealed record Not(ConditionalExpression Operand) : ConditionalExpression;

    /// <summary>Short-circuiting conjunction.</summary>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record And(ConditionalExpression Left, ConditionalExpression Right) : ConditionalExpression;

    /// <summary>Short-circuiting disjunction.</summary>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record Or(ConditionalExpression Left, ConditionalExpression Right) : ConditionalExpression;
}
