using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Bashkit.Builtins.Awk;

/// <summary>
/// Compiles AWK's extended regular expressions into .NET regexes.
/// </summary>
/// <remarks>
/// <para>
/// EREs are close to .NET syntax, so the translation only has to fix the places where they
/// genuinely differ: POSIX character classes (<c>[[:digit:]]</c>) have no .NET equivalent,
/// and a few metacharacters that are literal in an ERE would otherwise be interpreted.
/// </para>
/// <para>
/// Patterns are cached because AWK re-evaluates the same literal once per input record, and
/// every match runs under a timeout so a pathological pattern cannot hang the sandbox.
/// </para>
/// </remarks>
internal static class AwkRegexes
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Gets, compiling if needed, the regex for an ERE pattern.</summary>
    public static Regex Get(string pattern) =>
        Cache.GetOrAdd(pattern, static p =>
        {
            try
            {
                return new Regex(Translate(p), RegexOptions.None, MatchTimeout);
            }
            catch (ArgumentException)
            {
                // An invalid ERE matches nothing rather than aborting the program, which is
                // what gawk does for a dynamic pattern built at runtime.
                return new Regex(Regex.Escape(p), RegexOptions.None, MatchTimeout);
            }
        });

    /// <summary>True when <paramref name="pattern"/> matches anywhere in <paramref name="text"/>.</summary>
    public static bool IsMatch(string text, string pattern)
    {
        try
        {
            return Get(pattern).IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Rewrites an ERE into .NET regex syntax.</summary>
    private static string Translate(string pattern)
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
    private static int TranslateBracket(StringBuilder builder, string pattern, int start)
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
            if (pattern[i] == '[' && i + 1 < pattern.Length && pattern[i + 1] == ':')
            {
                var close = pattern.IndexOf(":]", i + 2, StringComparison.Ordinal);

                if (close > 0)
                {
                    builder.Append(ExpandClass(pattern[(i + 2)..close]));
                    i = close + 2;
                    continue;
                }
            }

            if (pattern[i] == '\\' && i + 1 < pattern.Length)
            {
                builder.Append(pattern[i]).Append(pattern[i + 1]);
                i += 2;
                continue;
            }

            // `[` inside a class is literal in an ERE but opens nothing in .NET either;
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

    private static string ExpandClass(string name) => name switch
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
        _ => string.Empty,
    };
}
