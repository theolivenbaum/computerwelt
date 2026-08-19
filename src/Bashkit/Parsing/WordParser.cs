using System.Globalization;
using System.Text;

namespace Bashkit.Parsing;

/// <summary>
/// Turns a word's raw source text into structured <see cref="WordPart"/>s.
/// </summary>
/// <remarks>
/// Splitting this out of the lexer keeps quote handling in one place. The lexer decides
/// only where a word ends; every question about what the word <i>means</i> — which parts
/// are literal, which expand, which were quoted and therefore escape word splitting — is
/// answered here.
/// </remarks>
public static class WordParser
{
    /// <summary>Parses raw word text into a structured word.</summary>
    public static Word Parse(string raw)
    {
        if (raw.Length == 0)
        {
            return Word.Empty;
        }

        var parts = new List<WordPart>();
        var literal = new StringBuilder();
        var i = 0;
        var atStart = true;

        void FlushLiteral(bool quoted)
        {
            if (literal.Length > 0)
            {
                parts.Add(new WordPart.Literal(literal.ToString()) { Quoted = quoted });
                literal.Clear();
            }
        }

        while (i < raw.Length)
        {
            var c = raw[i];

            switch (c)
            {
                case '~' when atStart:
                {
                    var end = i + 1;
                    while (end < raw.Length && raw[end] != '/' && raw[end] != ':')
                    {
                        end++;
                    }

                    FlushLiteral(quoted: false);
                    parts.Add(new WordPart.Tilde(raw[(i + 1)..end]));
                    i = end;
                    atStart = false;
                    continue;
                }

                case '\\':
                    // Outside quotes a backslash escapes exactly one character, and the
                    // result is quoted: `a\ b` is one word, not two.
                    if (i + 1 < raw.Length)
                    {
                        FlushLiteral(quoted: false);
                        parts.Add(new WordPart.Literal(raw[i + 1].ToString()) { Quoted = true });
                        i += 2;
                    }
                    else
                    {
                        literal.Append('\\');
                        i++;
                    }

                    atStart = false;
                    continue;

                case '\'':
                {
                    var end = raw.IndexOf('\'', i + 1);
                    if (end < 0)
                    {
                        end = raw.Length;
                    }

                    FlushLiteral(quoted: false);
                    parts.Add(new WordPart.Literal(raw[(i + 1)..end]) { Quoted = true });
                    i = Math.Min(end + 1, raw.Length);
                    atStart = false;
                    continue;
                }

                case '"':
                {
                    FlushLiteral(quoted: false);
                    i = ParseDoubleQuoted(raw, i + 1, parts);
                    atStart = false;
                    continue;
                }

                case '`':
                {
                    var end = FindBacktickEnd(raw, i + 1);
                    FlushLiteral(quoted: false);
                    parts.Add(new WordPart.CommandSubstitution(Unescape(raw[(i + 1)..end]), Backticks: true));
                    i = Math.Min(end + 1, raw.Length);
                    atStart = false;
                    continue;
                }

                case '$' when i + 1 < raw.Length:
                {
                    var consumed = ParseDollar(raw, i, quoted: false, parts, literal);
                    if (consumed > 0)
                    {
                        i += consumed;
                        atStart = false;
                        continue;
                    }

                    literal.Append(c);
                    i++;
                    atStart = false;
                    continue;
                }

                default:
                    literal.Append(c);
                    i++;
                    atStart = false;
                    continue;
            }
        }

        FlushLiteral(quoted: false);
        return new Word(parts);
    }

    /// <summary>True when the raw text contains a quote that survives to the parts.</summary>
    public static bool HasQuotes(string raw)
    {
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c is '\'' or '"')
            {
                return true;
            }

            if (c == '\\')
            {
                i++;
            }
        }

        return false;
    }

    private static int ParseDoubleQuoted(string raw, int start, List<WordPart> parts)
    {
        var literal = new StringBuilder();
        var i = start;

        void Flush()
        {
            if (literal.Length > 0)
            {
                parts.Add(new WordPart.Literal(literal.ToString()) { Quoted = true });
                literal.Clear();
            }
        }

        while (i < raw.Length && raw[i] != '"')
        {
            var c = raw[i];

            if (c == '\\' && i + 1 < raw.Length)
            {
                var next = raw[i + 1];

                // Inside double quotes a backslash is literal unless it precedes one of
                // the four characters that remain special there.
                if (next is '$' or '`' or '"' or '\\')
                {
                    literal.Append(next);
                    i += 2;
                    continue;
                }

                if (next == '\n')
                {
                    i += 2;
                    continue;
                }

                literal.Append(c);
                i++;
                continue;
            }

            if (c == '`')
            {
                var end = FindBacktickEnd(raw, i + 1);
                Flush();
                parts.Add(new WordPart.CommandSubstitution(Unescape(raw[(i + 1)..end]), Backticks: true) { Quoted = true });
                i = Math.Min(end + 1, raw.Length);
                continue;
            }

            if (c == '$' && i + 1 < raw.Length)
            {
                var consumed = ParseDollar(raw, i, quoted: true, parts, literal);
                if (consumed > 0)
                {
                    i += consumed;
                    continue;
                }
            }

            literal.Append(c);
            i++;
        }

        Flush();

        // An empty pair of quotes still produces an empty field, so it must leave a part.
        if (parts.Count == 0 || i == start)
        {
            parts.Add(new WordPart.Literal(string.Empty) { Quoted = true });
        }

        return Math.Min(i + 1, raw.Length);
    }

    /// <summary>
    /// Parses one <c>$</c>-form starting at <paramref name="index"/>. Returns the number of
    /// characters consumed, or 0 when the <c>$</c> is not the start of an expansion.
    /// </summary>
    private static int ParseDollar(string raw, int index, bool quoted, List<WordPart> parts, StringBuilder literal)
    {
        void Flush()
        {
            if (literal.Length > 0)
            {
                parts.Add(new WordPart.Literal(literal.ToString()) { Quoted = quoted });
                literal.Clear();
            }
        }

        var next = raw[index + 1];

        // $((...)) — arithmetic
        if (next == '(' && index + 2 < raw.Length && raw[index + 2] == '(')
        {
            var end = FindBalanced(raw, index + 2, '(', ')');
            if (end > 0 && end + 1 < raw.Length && raw[end + 1] == ')')
            {
                Flush();
                parts.Add(new WordPart.Arithmetic(raw[(index + 3)..end]) { Quoted = quoted });
                return end + 2 - index;
            }
        }

        // $(...) — command substitution
        if (next == '(')
        {
            var end = FindBalanced(raw, index + 1, '(', ')');
            if (end > 0)
            {
                Flush();
                parts.Add(new WordPart.CommandSubstitution(raw[(index + 2)..end]) { Quoted = quoted });
                return end + 1 - index;
            }
        }

        // ${...} — braced parameter expansion
        if (next == '{')
        {
            var end = FindBalanced(raw, index + 1, '{', '}');
            if (end > 0)
            {
                Flush();
                parts.Add(ParseBracedParameter(raw[(index + 2)..end]) with { Quoted = quoted });
                return end + 1 - index;
            }
        }

        // $'...' — ANSI-C quoting, decoded here because its escapes are not shell escapes.
        if (next == '\'')
        {
            var end = raw.IndexOf('\'', index + 2);
            if (end < 0)
            {
                end = raw.Length;
            }

            Flush();
            parts.Add(new WordPart.Literal(DecodeAnsiC(raw[(index + 2)..end])) { Quoted = true });
            return Math.Min(end + 1, raw.Length) - index;
        }

        // $name
        if (char.IsAsciiLetter(next) || next == '_')
        {
            var end = index + 1;
            while (end < raw.Length && (char.IsAsciiLetterOrDigit(raw[end]) || raw[end] == '_'))
            {
                end++;
            }

            Flush();
            parts.Add(new WordPart.Parameter(raw[(index + 1)..end]) { Quoted = quoted });
            return end - index;
        }

        // $1 .. $9, and the special parameters.
        if (char.IsAsciiDigit(next) || next is '@' or '*' or '#' or '?' or '-' or '$' or '!' or '_')
        {
            Flush();
            parts.Add(new WordPart.Parameter(next.ToString()) { Quoted = quoted });
            return 2;
        }

        return 0;
    }

    /// <summary>
    /// Parses the interior of <c>${...}</c>, which carries all of the modifier syntax.
    /// </summary>
    private static WordPart.Parameter ParseBracedParameter(string body)
    {
        if (body.Length == 0)
        {
            return new WordPart.Parameter(string.Empty);
        }

        var indirect = false;
        var lengthOf = false;
        var i = 0;

        if (body[0] == '#' && body.Length > 1)
        {
            // `${#}` is the positional count; `${#name}` is a length.
            lengthOf = true;
            i = 1;
        }
        else if (body[0] == '!' && body.Length > 1)
        {
            indirect = true;
            i = 1;
        }

        var nameStart = i;
        while (i < body.Length && (char.IsAsciiLetterOrDigit(body[i]) || body[i] == '_'))
        {
            i++;
        }

        // Special parameters are single characters that are not name characters.
        if (i == nameStart && i < body.Length && body[i] is '@' or '*' or '?' or '$' or '!' or '-' or '#')
        {
            i++;
        }

        var name = body[nameStart..i];

        string? index = null;
        if (i < body.Length && body[i] == '[')
        {
            var close = FindBalanced(body, i, '[', ']');
            if (close > 0)
            {
                index = body[(i + 1)..close];
                i = close + 1;
            }
        }

        if (i >= body.Length)
        {
            return new WordPart.Parameter(name, Index: index, IndirectRef: indirect, LengthOf: lengthOf);
        }

        var rest = body[i..];
        var (op, argText, replacementText) = ParseOperator(rest);

        return new WordPart.Parameter(
            name,
            op,
            argText is null ? null : Parse(argText),
            index,
            indirect,
            lengthOf)
        {
            Replacement = replacementText is null ? null : Parse(replacementText),
        };
    }

    private static (ParameterOp Op, string? Argument, string? Replacement) ParseOperator(string rest)
    {
        // Order matters: the two-character forms must be tested before their prefixes.
        if (rest.StartsWith(":-", StringComparison.Ordinal))
        {
            return (ParameterOp.UseDefault, rest[2..], null);
        }

        if (rest.StartsWith(":=", StringComparison.Ordinal))
        {
            return (ParameterOp.AssignDefault, rest[2..], null);
        }

        if (rest.StartsWith(":?", StringComparison.Ordinal))
        {
            return (ParameterOp.ErrorIfUnset, rest[2..], null);
        }

        if (rest.StartsWith(":+", StringComparison.Ordinal))
        {
            return (ParameterOp.UseAlternate, rest[2..], null);
        }

        if (rest.StartsWith("##", StringComparison.Ordinal))
        {
            return (ParameterOp.RemoveLargestPrefix, rest[2..], null);
        }

        if (rest.StartsWith("%%", StringComparison.Ordinal))
        {
            return (ParameterOp.RemoveLargestSuffix, rest[2..], null);
        }

        if (rest.StartsWith("^^", StringComparison.Ordinal))
        {
            return (ParameterOp.UpperAll, rest[2..], null);
        }

        if (rest.StartsWith(",,", StringComparison.Ordinal))
        {
            return (ParameterOp.LowerAll, rest[2..], null);
        }

        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            var (pattern, replacement) = SplitReplacement(rest[2..]);
            return (ParameterOp.ReplaceAll, pattern, replacement);
        }

        if (rest.StartsWith("/#", StringComparison.Ordinal))
        {
            var (pattern, replacement) = SplitReplacement(rest[2..]);
            return (ParameterOp.ReplacePrefix, pattern, replacement);
        }

        if (rest.StartsWith("/%", StringComparison.Ordinal))
        {
            var (pattern, replacement) = SplitReplacement(rest[2..]);
            return (ParameterOp.ReplaceSuffix, pattern, replacement);
        }

        return rest[0] switch
        {
            '-' => (ParameterOp.UseDefaultUnsetOnly, rest[1..], null),
            '=' => (ParameterOp.AssignDefaultUnsetOnly, rest[1..], null),
            '?' => (ParameterOp.ErrorIfUnsetOnly, rest[1..], null),
            '+' => (ParameterOp.UseAlternateSetOnly, rest[1..], null),
            '#' => (ParameterOp.RemoveSmallestPrefix, rest[1..], null),
            '%' => (ParameterOp.RemoveSmallestSuffix, rest[1..], null),
            '^' => (ParameterOp.UpperFirst, rest[1..], null),
            ',' => (ParameterOp.LowerFirst, rest[1..], null),
            '@' => (ParameterOp.Transform, rest[1..], null),
            ':' => (ParameterOp.Substring, rest[1..], null),
            '/' => SplitReplacement(rest[1..]) is var (p, r) ? (ParameterOp.ReplaceFirst, p, r) : default,
            _ => (ParameterOp.None, null, null),
        };
    }

    /// <summary>
    /// Splits <c>pattern/replacement</c> at the first unescaped <c>/</c>. A missing
    /// separator means "replace with nothing", which is how <c>${v//x}</c> deletes.
    /// </summary>
    private static (string Pattern, string Replacement) SplitReplacement(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '/')
            {
                return (text[..i], text[(i + 1)..]);
            }
        }

        return (text, string.Empty);
    }

    /// <summary>
    /// Finds the index of the character closing the construct that opens at
    /// <paramref name="openIndex"/>, skipping quoted regions. Returns -1 when unbalanced.
    /// </summary>
    internal static int FindBalanced(string text, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c == '\'')
            {
                var end = text.IndexOf('\'', i + 1);
                i = end < 0 ? text.Length : end;
                continue;
            }

            if (c == '"')
            {
                var end = FindDoubleQuoteEnd(text, i + 1);
                i = end;
                continue;
            }

            if (c == open)
            {
                depth++;
            }
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int FindDoubleQuoteEnd(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                return i;
            }
        }

        return text.Length;
    }

    private static int FindBacktickEnd(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '`')
            {
                return i;
            }
        }

        return text.Length;
    }

    private static string Unescape(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '$' or '`' or '\\')
            {
                builder.Append(text[++i]);
                continue;
            }

            builder.Append(text[i]);
        }

        return builder.ToString();
    }

    /// <summary>Decodes the escape sequences <c>$'...'</c> recognizes.</summary>
    internal static string DecodeAnsiC(string text)
    {
        var builder = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                builder.Append(text[i]);
                continue;
            }

            var c = text[++i];
            switch (c)
            {
                case 'a': builder.Append('\a'); break;
                case 'b': builder.Append('\b'); break;
                case 'e' or 'E': builder.Append('\x1b'); break;
                case 'f': builder.Append('\f'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'v': builder.Append('\v'); break;
                case '\\': builder.Append('\\'); break;
                case '\'': builder.Append('\''); break;
                case '"': builder.Append('"'); break;
                case '?': builder.Append('?'); break;

                case 'x':
                {
                    var digits = TakeWhile(text, i + 1, 2, Uri.IsHexDigit);
                    if (digits.Length == 0)
                    {
                        builder.Append("\\x");
                        break;
                    }

                    builder.Append((char)Convert.ToInt32(digits, 16));
                    i += digits.Length;
                    break;
                }

                case 'u' or 'U':
                {
                    var max = c == 'u' ? 4 : 8;
                    var digits = TakeWhile(text, i + 1, max, Uri.IsHexDigit);
                    if (digits.Length == 0)
                    {
                        builder.Append('\\').Append(c);
                        break;
                    }

                    builder.Append(char.ConvertFromUtf32(Convert.ToInt32(digits, 16)));
                    i += digits.Length;
                    break;
                }

                case >= '0' and <= '7':
                {
                    var digits = TakeWhile(text, i, 3, static ch => ch is >= '0' and <= '7');
                    builder.Append((char)Convert.ToInt32(digits, 8));
                    i += digits.Length - 1;
                    break;
                }

                default:
                    builder.Append('\\').Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string TakeWhile(string text, int start, int max, Func<char, bool> predicate)
    {
        var end = start;
        while (end < text.Length && end - start < max && predicate(text[end]))
        {
            end++;
        }

        return text[start..end];
    }

    /// <summary>Formats an integer the way the shell does, with no locale influence.</summary>
    internal static string FormatNumber(long value) => value.ToString(CultureInfo.InvariantCulture);
}
