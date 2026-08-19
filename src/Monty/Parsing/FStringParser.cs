using System.Text;

namespace Monty.Parsing;

/// <summary>
/// Expands an f-string body into literal and interpolated parts.
/// </summary>
/// <remarks>
/// The body arrives undecoded because a decoded one would be ambiguous: an escaped brace
/// and a real field opener would look the same. Fields are found first, then each literal
/// run is decoded on its own.
/// </remarks>
public static class FStringParser
{
    /// <summary>Parses an f-string body into a <see cref="FormattedString"/>.</summary>
    public static FormattedString Parse(string body)
    {
        var parts = new List<FormatPart>();
        var literal = new StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                parts.Add(new FormatPart(Tokenizer.DecodeEscapes(literal.ToString(), isBytes: false), null, '\0', null));
                literal.Clear();
            }
        }

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];

            // `{{` and `}}` are escaped braces, not fields.
            if (c == '{' && i + 1 < body.Length && body[i + 1] == '{')
            {
                literal.Append('{');
                i++;
                continue;
            }

            if (c == '}' && i + 1 < body.Length && body[i + 1] == '}')
            {
                literal.Append('}');
                i++;
                continue;
            }

            if (c != '{')
            {
                literal.Append(c);
                continue;
            }

            var end = FindFieldEnd(body, i);

            if (end < 0)
            {
                throw new PythonSyntaxError("f-string: expecting '}'", 1, i);
            }

            FlushLiteral();
            parts.Add(ParseField(body[(i + 1)..end]));
            i = end;
        }

        FlushLiteral();
        return new FormattedString(parts);
    }

    /// <summary>
    /// Finds the brace closing the field opened at <paramref name="open"/>, skipping
    /// nested brackets and quoted regions so that <c>{d['}']}</c> works.
    /// </summary>
    private static int FindFieldEnd(string body, int open)
    {
        var depth = 0;
        var quote = '\0';

        for (var i = open; i < body.Length; i++)
        {
            var c = body[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    break;

                case '{' or '[' or '(':
                    depth++;
                    break;

                case '}' when depth == 1:
                    return i;

                case '}' or ']' or ')':
                    depth--;
                    break;
            }
        }

        return -1;
    }

    /// <summary>Parses one field: <c>expression[=][!conversion][:spec]</c>.</summary>
    private static FormatPart ParseField(string field)
    {
        var (expressionText, conversion, specText) = SplitField(field);

        // The `=` debug form prints the source text and then the value.
        var debug = expressionText.EndsWith('=');
        if (debug)
        {
            expressionText = expressionText[..^1];
        }

        var expression = ParseExpression(expressionText);

        // A spec may itself contain fields: `{x:{width}}`.
        Expression? spec = specText is null
            ? null
            : specText.Contains('{', StringComparison.Ordinal)
                ? Parse(specText)
                : new Literal(specText);

        if (!debug)
        {
            return new FormatPart(null, expression, conversion, spec);
        }

        // `f'{x=}'` is exactly `f'x={x!r}'`, so it becomes two parts — but a FormatPart
        // holds one, so the literal half is folded into the expression by wrapping.
        return new FormatPart(null, expression, conversion == '\0' ? 'r' : conversion, spec)
        {
            Literal = expressionText + "=",
        };
    }

    private static (string Expression, char Conversion, string? Spec) SplitField(string field)
    {
        var depth = 0;
        var quote = '\0';

        for (var i = 0; i < field.Length; i++)
        {
            var c = field[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    continue;

                case '{' or '[' or '(':
                    depth++;
                    continue;

                case '}' or ']' or ')':
                    depth--;
                    continue;

                case '!' when depth == 0 && i + 1 < field.Length && field[i + 1] != '=':
                {
                    var conversion = field[i + 1];
                    var rest = field[(i + 2)..];
                    var spec = rest.StartsWith(':') ? rest[1..] : null;
                    return (field[..i], conversion, spec);
                }

                case ':' when depth == 0:
                    return (field[..i], '\0', field[(i + 1)..]);
            }
        }

        return (field, '\0', null);
    }

    private static Expression ParseExpression(string text)
    {
        var module = Parser.Parse(text.Trim());

        if (module.Body.Count != 1 || module.Body[0] is not ExpressionStatement statement)
        {
            throw new PythonSyntaxError("f-string: invalid expression", 1, 0);
        }

        return statement.Value;
    }
}
