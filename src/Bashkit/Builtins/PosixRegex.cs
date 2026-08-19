using System.Text;

namespace Bashkit.Builtins;

/// <summary>
/// Translates POSIX regular expressions into .NET syntax.
/// </summary>
/// <remarks>
/// <para>
/// The two POSIX flavours differ from .NET in opposite directions. In a basic regular
/// expression <c>\(</c> groups and a bare <c>(</c> is literal — the inverse of .NET — while
/// an extended one is already close, needing only the pieces .NET has no equivalent for.
/// Both share bracket expressions, where POSIX classes such as <c>[[:digit:]]</c> must be
/// expanded and where nearly every metacharacter is literal.
/// </para>
/// <para>
/// Keeping both translations here means <c>grep</c>, <c>sed</c>, <c>expr</c> and <c>awk</c>
/// agree on what a pattern means, which is the whole point of a shared regex dialect.
/// </para>
/// </remarks>
internal static class PosixRegex
{
    /// <summary>Translates a pattern in either flavour.</summary>
    /// <param name="pattern">The POSIX pattern.</param>
    /// <param name="extended">Whether it is an ERE rather than a BRE.</param>
    public static string Translate(string pattern, bool extended) =>
        extended ? TranslateExtended(pattern) : TranslateBasic(pattern);

    /// <summary>Translates a basic regular expression.</summary>
    public static string TranslateBasic(string pattern)
    {
        var builder = new StringBuilder(pattern.Length);

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];

            if (c == '[')
            {
                i = TranslateBracket(builder, pattern, i);
                continue;
            }

            if (c != '\\' || i + 1 >= pattern.Length)
            {
                if (c is '(' or ')' or '{' or '}' or '+' or '?' or '|')
                {
                    builder.Append('\\');
                }

                builder.Append(c);
                continue;
            }

            var next = pattern[++i];

            if (next is '(' or ')' or '{' or '}' or '+' or '?' or '|')
            {
                builder.Append(next);
                continue;
            }

            builder.Append('\\').Append(next);
        }

        return builder.ToString();
    }

    /// <summary>Translates an extended regular expression.</summary>
    public static string TranslateExtended(string pattern)
    {
        var builder = new StringBuilder(pattern.Length);

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];

            if (c == '\\' && i + 1 < pattern.Length)
            {
                builder.Append(c).Append(pattern[++i]);
                continue;
            }

            if (c == '[')
            {
                i = TranslateBracket(builder, pattern, i);
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Copies a bracket expression, expanding POSIX classes. Returns the index of the
    /// closing bracket.
    /// </summary>
    public static int TranslateBracket(StringBuilder builder, string pattern, int start)
    {
        var i = start + 1;
        builder.Append('[');

        if (i < pattern.Length && pattern[i] == '^')
        {
            builder.Append('^');
            i++;
        }

        // A `]` immediately after the opening bracket is literal.
        if (i < pattern.Length && pattern[i] == ']')
        {
            builder.Append("\\]");
            i++;
        }

        while (i < pattern.Length && pattern[i] != ']')
        {
            if (pattern[i] == '[' && i + 1 < pattern.Length && pattern[i + 1] is ':' or '.' or '=')
            {
                var kind = pattern[i + 1];
                var close = pattern.IndexOf($"{kind}]", i + 2, StringComparison.Ordinal);

                if (close > 0)
                {
                    var inner = pattern[(i + 2)..close];

                    // `[.x.]` and `[=x=]` are collating elements; with no locale to consult
                    // they stand for the character itself.
                    builder.Append(kind == ':' ? ExpandClass(inner) : Escape(inner));
                    i = close + 2;
                    continue;
                }
            }

            if (pattern[i] == '\\' && i + 1 < pattern.Length)
            {
                // POSIX leaves a backslash literal inside a bracket expression, but every
                // tool in practice honours `\]`, `\\` and the usual escapes, so they are
                // passed through for .NET to read the same way.
                builder.Append(pattern[i]).Append(pattern[i + 1]);
                i += 2;
                continue;
            }

            // `[` is literal inside a POSIX class but would open nothing in .NET either;
            // escaping it keeps both readings identical.
            if (pattern[i] == '[')
            {
                builder.Append("\\[");
                i++;
                continue;
            }

            builder.Append(pattern[i]);
            i++;
        }

        builder.Append(']');
        return i;
    }

    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (c is '\\' or ']' or '^' or '-' or '[')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Expands a POSIX class name to its .NET character-class body.</summary>
    public static string ExpandClass(string name) => name switch
    {
        "alpha" => "a-zA-Z",
        "digit" => "0-9",
        "alnum" => "a-zA-Z0-9",
        "upper" => "A-Z",
        "lower" => "a-z",
        "space" => @"\s",
        "blank" => @" \t",
        "punct" => @"!-/:-@\[-`{-~",
        "print" => @" -~",
        "graph" => "!-~",
        "cntrl" => @"\x00-\x1f\x7f",
        "xdigit" => "0-9A-Fa-f",
        "word" => @"\w",
        _ => string.Empty,
    };
}
