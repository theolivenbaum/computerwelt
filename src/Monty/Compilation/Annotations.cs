using System.Text;
using Monty.Parsing;
using Monty.Runtime;

namespace Monty.Compilation;

/// <summary>
/// Turns an annotation expression back into source text.
/// </summary>
/// <remarks>
/// Class annotations are stringized rather than evaluated, so a name that exists only for a
/// type checker — <c>ClassVar[int]</c>, a forward reference — costs nothing at run time and
/// raises nothing. What is stored is a canonical unparse rather than the raw source: spacing
/// and line breaks are discarded, and every literal is rebuilt from its value, which is what
/// CPython does under <c>from __future__ import annotations</c>.
/// </remarks>
internal static class Annotations
{
    /// <summary>Renders an expression as the text an annotation stores.</summary>
    /// <param name="expression">The annotation.</param>
    /// <returns>The canonical source text.</returns>
    public static string Unparse(Expression expression) => expression switch
    {
        Name name => name.Id,
        Parsing.Attribute attribute => Unparse(attribute.Value) + "." + attribute.AttributeName,

        // A subscript's index is written bare when it is a tuple: `dict[str, int]`, not
        // `dict[(str, int)]`.
        Subscript subscript => Unparse(subscript.Value) + "[" + Index(subscript.Index) + "]",

        TupleExpr tuple => "(" + Index(tuple) + (tuple.Elements.Count == 1 ? ",)" : ")"),
        ListExpr list => "[" + Join(list.Elements) + "]",
        SetExpr set => "{" + Join(set.Elements) + "}",
        Starred starred => "*" + Unparse(starred.Value),
        Slice slice => Bound(slice.Lower) + ":" + Bound(slice.Upper)
            + (slice.Step is null ? string.Empty : ":" + Unparse(slice.Step)),

        BinaryOp binary => Unparse(binary.Left) + " " + binary.Operator + " " + Unparse(binary.Right),
        UnaryOp unary => unary.Operator + (unary.Operator == "not" ? " " : string.Empty) + Unparse(unary.Operand),
        BoolOp boolean => string.Join(" " + boolean.Operator + " ", boolean.Values.Select(Unparse)),

        Call call => Unparse(call.Function) + "(" + Join(call.Arguments) + ")",
        Literal literal => Constant(literal),
        FormattedString formatted => Interpolated(formatted),

        // Anything else is spelled by its node name, which is enough for a checker to read
        // and better than losing the annotation entirely.
        _ => expression.GetType().Name,
    };

    private static string Bound(Expression? value) => value is null ? string.Empty : Unparse(value);

    private static string Index(Expression index) =>
        index is TupleExpr tuple ? Join(tuple.Elements) : Unparse(index);

    private static string Join(IEnumerable<Expression> elements) => string.Join(", ", elements.Select(Unparse));

    /// <summary>Renders a literal the way its <c>repr</c> would.</summary>
    private static string Constant(Literal literal) => literal.Value switch
    {
        null => "None",
        bool flag => flag ? "True" : "False",

        // The `u` prefix means nothing but is canonical, so it is kept where it was written.
        string text => (literal.HasUnicodePrefix ? "u" : string.Empty) + PyStr.Quote(text),
        byte[] bytes => new PyBytes(bytes).Repr(),
        System.Numerics.BigInteger number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double number => new PyFloat(number).Repr(),
        var other => other.ToString() ?? string.Empty,
    };

    /// <summary>Renders an f-string, whose quoting rules are the same as a plain string's.</summary>
    private static string Interpolated(FormattedString formatted)
    {
        var inner = new StringBuilder();

        foreach (var part in formatted.Parts)
        {
            inner.Append(part.Literal);

            if (part.Value is null)
            {
                continue;
            }

            inner.Append('{').Append(Unparse(part.Value));

            if (part.Conversion != '\0')
            {
                inner.Append('!').Append(part.Conversion);
            }

            if (part.FormatSpec is { } spec)
            {
                inner.Append(':').Append(spec is Literal { Value: string text } ? text : Unparse(spec));
            }

            inner.Append('}');
        }

        return "f" + PyStr.Quote(inner.ToString());
    }
}
