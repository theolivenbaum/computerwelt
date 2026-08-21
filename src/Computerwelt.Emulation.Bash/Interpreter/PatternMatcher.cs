namespace Computerwelt.Emulation.Bash.Interpreter;

/// <summary>
/// Matches shell patterns (globs), the same syntax used by <c>case</c>, <c>[[ == ]]</c>,
/// <c>${v#pat}</c> and pathname expansion.
/// </summary>
/// <remarks>
/// <para>
/// Shell patterns are not regular expressions and translating to one is a trap: a pattern
/// is arbitrary user text, so any character can appear raw, and <c>[</c> without a closing
/// bracket is a literal. A direct backtracking matcher avoids an escaping layer that would
/// have to be exactly right in both directions.
/// </para>
/// <para>
/// The matcher is also used for the <c>#</c>/<c>%</c>/<c>/</c> parameter operators, which
/// need shortest and longest <i>prefix</i> and <i>suffix</i> matches rather than a whole
/// string match — hence <see cref="MatchPrefix"/> and <see cref="MatchSuffix"/>.
/// </para>
/// </remarks>
public static class PatternMatcher
{
    /// <summary>True when <paramref name="text"/> matches <paramref name="pattern"/> entirely.</summary>
    public static bool IsMatch(string text, string pattern, bool caseInsensitive = false, bool extGlob = false)
    {
        var options = new MatchOptions(caseInsensitive, extGlob, MatchLeadingDot: true);
        return Match(text, 0, pattern, 0, options) == text.Length;
    }

    /// <summary>
    /// Length of the prefix of <paramref name="text"/> matching <paramref name="pattern"/>,
    /// or -1 when there is none. Used by <c>${v#pat}</c> and <c>${v##pat}</c>.
    /// </summary>
    public static int MatchPrefix(string text, string pattern, bool longest, bool caseInsensitive = false, bool extGlob = false)
    {
        var options = new MatchOptions(caseInsensitive, extGlob, MatchLeadingDot: true);
        var best = -1;

        for (var end = longest ? text.Length : 0; longest ? end >= 0 : end <= text.Length; end += longest ? -1 : 1)
        {
            if (Match(text[..end], 0, pattern, 0, options) == end)
            {
                best = end;
                break;
            }
        }

        return best;
    }

    /// <summary>
    /// Start index of the suffix of <paramref name="text"/> matching
    /// <paramref name="pattern"/>, or -1 when there is none. Used by <c>${v%pat}</c>.
    /// </summary>
    public static int MatchSuffix(string text, string pattern, bool longest, bool caseInsensitive = false, bool extGlob = false)
    {
        var options = new MatchOptions(caseInsensitive, extGlob, MatchLeadingDot: true);

        for (var start = longest ? 0 : text.Length; longest ? start <= text.Length : start >= 0; start += longest ? 1 : -1)
        {
            var candidate = text[start..];
            if (Match(candidate, 0, pattern, 0, options) == candidate.Length)
            {
                return start;
            }
        }

        return -1;
    }

    /// <summary>
    /// Finds the first substring matching <paramref name="pattern"/>, preferring the
    /// longest match at the earliest position. Used by <c>${v/pat/rep}</c>.
    /// </summary>
    public static (int Start, int Length)? FindSubstring(string text, string pattern, int from, bool caseInsensitive = false, bool extGlob = false)
    {
        var options = new MatchOptions(caseInsensitive, extGlob, MatchLeadingDot: true);

        for (var start = from; start <= text.Length; start++)
        {
            for (var end = text.Length; end >= start; end--)
            {
                var slice = text[start..end];
                if (Match(slice, 0, pattern, 0, options) == slice.Length)
                {
                    return (start, end - start);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="pattern"/> contains an unquoted metacharacter, and so
    /// needs globbing at all. Avoids a filesystem walk for ordinary filenames.
    /// </summary>
    public static bool HasMetacharacters(string pattern, bool extGlob = false)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '\\':
                    i++;
                    break;
                case '*' or '?' or '[':
                    return true;
                case '+' or '@' or '!' when extGlob && i + 1 < pattern.Length && pattern[i + 1] == '(':
                    return true;
            }
        }

        return false;
    }

    private readonly record struct MatchOptions(bool CaseInsensitive, bool ExtGlob, bool MatchLeadingDot);

    /// <summary>
    /// Matches <paramref name="pattern"/> from <paramref name="p"/> against
    /// <paramref name="text"/> from <paramref name="t"/>, returning the index in
    /// <paramref name="text"/> one past the match, or -1.
    /// </summary>
    private static int Match(string text, int t, string pattern, int p, MatchOptions options)
    {
        while (p < pattern.Length)
        {
            var pc = pattern[p];

            switch (pc)
            {
                // Must precede the plain `?`/`*` cases: `?(a|b)` is an extglob group, not
                // a single-character wildcard followed by a literal parenthesis.
                case '?' or '*' or '+' or '@' or '!'
                    when options.ExtGlob && p + 1 < pattern.Length && pattern[p + 1] == '(':
                    return MatchExtGlob(text, t, pattern, p, options);

                case '\\' when p + 1 < pattern.Length:
                    if (t >= text.Length || !CharsEqual(text[t], pattern[p + 1], options))
                    {
                        return -1;
                    }

                    t++;
                    p += 2;
                    continue;

                case '?':
                    if (t >= text.Length)
                    {
                        return -1;
                    }

                    t++;
                    p++;
                    continue;

                case '*':
                {
                    // Collapse runs of `*`, then try every split point from longest down.
                    while (p < pattern.Length && pattern[p] == '*')
                    {
                        p++;
                    }

                    if (p == pattern.Length)
                    {
                        return text.Length;
                    }

                    for (var skip = text.Length; skip >= t; skip--)
                    {
                        var result = Match(text, skip, pattern, p, options);
                        if (result >= 0)
                        {
                            return result;
                        }
                    }

                    return -1;
                }

                case '[':
                {
                    var consumed = MatchBracket(text, t, pattern, p, options, out var nextP);
                    if (consumed < 0)
                    {
                        // An unterminated `[` is a literal bracket.
                        if (t >= text.Length || !CharsEqual(text[t], '[', options))
                        {
                            return -1;
                        }

                        t++;
                        p++;
                        continue;
                    }

                    t = consumed;
                    p = nextP;
                    continue;
                }

                default:
                    if (t >= text.Length || !CharsEqual(text[t], pc, options))
                    {
                        return -1;
                    }

                    t++;
                    p++;
                    continue;
            }
        }

        return t == text.Length ? t : -1;
    }

    /// <summary>
    /// Matches a bracket expression. Returns the new text index, or -1 when the bracket is
    /// unterminated (in which case the caller treats <c>[</c> as a literal).
    /// </summary>
    private static int MatchBracket(string text, int t, string pattern, int p, MatchOptions options, out int nextP)
    {
        nextP = p;
        var i = p + 1;

        var negated = false;
        if (i < pattern.Length && (pattern[i] == '!' || pattern[i] == '^'))
        {
            negated = true;
            i++;
        }

        // A `]` immediately after the opening bracket is a literal member.
        var first = true;
        var matched = false;
        var c = t < text.Length ? text[t] : '\0';

        while (i < pattern.Length && (pattern[i] != ']' || first))
        {
            first = false;

            // POSIX character classes: [[:alpha:]]
            if (pattern[i] == '[' && i + 1 < pattern.Length && pattern[i + 1] == ':')
            {
                var close = pattern.IndexOf(":]", i + 2, StringComparison.Ordinal);
                if (close > 0)
                {
                    var className = pattern[(i + 2)..close];
                    if (t < text.Length && MatchesClass(c, className))
                    {
                        matched = true;
                    }

                    i = close + 2;
                    continue;
                }
            }

            var low = pattern[i];
            if (low == '\\' && i + 1 < pattern.Length)
            {
                low = pattern[++i];
            }

            if (i + 2 < pattern.Length && pattern[i + 1] == '-' && pattern[i + 2] != ']')
            {
                var high = pattern[i + 2];
                if (t < text.Length && InRange(c, low, high, options))
                {
                    matched = true;
                }

                i += 3;
                continue;
            }

            if (t < text.Length && CharsEqual(c, low, options))
            {
                matched = true;
            }

            i++;
        }

        if (i >= pattern.Length)
        {
            return -1; // unterminated
        }

        nextP = i + 1;

        if (t >= text.Length)
        {
            return -1;
        }

        return matched != negated ? t + 1 : -1;
    }

    /// <summary>
    /// Matches an extglob group: <c>?(a|b)</c>, <c>*(a)</c>, <c>+(a)</c>, <c>@(a)</c>,
    /// <c>!(a)</c>.
    /// </summary>
    private static int MatchExtGlob(string text, int t, string pattern, int p, MatchOptions options)
    {
        var op = pattern[p];
        var close = FindGroupEnd(pattern, p + 1);
        if (close < 0)
        {
            return -1;
        }

        var alternatives = SplitAlternatives(pattern[(p + 2)..close]);
        var rest = pattern[(close + 1)..];

        switch (op)
        {
            case '@':
            case '?':
            {
                // `?(...)` also allows zero occurrences.
                if (op == '?')
                {
                    var zero = Match(text, t, rest, 0, options);
                    if (zero >= 0)
                    {
                        return zero;
                    }
                }

                foreach (var alternative in alternatives)
                {
                    for (var end = text.Length; end >= t; end--)
                    {
                        var slice = text[t..end];
                        if (Match(slice, 0, alternative, 0, options) != slice.Length)
                        {
                            continue;
                        }

                        var result = Match(text, end, rest, 0, options);
                        if (result >= 0)
                        {
                            return result;
                        }
                    }
                }

                return -1;
            }

            case '*':
            case '+':
            {
                if (op == '*')
                {
                    var zero = Match(text, t, rest, 0, options);
                    if (zero >= 0)
                    {
                        return zero;
                    }
                }

                for (var end = text.Length; end > t; end--)
                {
                    if (!MatchesRepeated(text, t, end, alternatives, options))
                    {
                        continue;
                    }

                    var result = Match(text, end, rest, 0, options);
                    if (result >= 0)
                    {
                        return result;
                    }
                }

                return -1;
            }

            case '!':
            {
                for (var end = text.Length; end >= t; end--)
                {
                    var slice = text[t..end];
                    var anyMatch = alternatives.Any(a => Match(slice, 0, a, 0, options) == slice.Length);
                    if (anyMatch)
                    {
                        continue;
                    }

                    var result = Match(text, end, rest, 0, options);
                    if (result >= 0)
                    {
                        return result;
                    }
                }

                return -1;
            }

            default:
                return -1;
        }
    }

    private static bool MatchesRepeated(string text, int start, int end, List<string> alternatives, MatchOptions options)
    {
        if (start == end)
        {
            return true;
        }

        foreach (var alternative in alternatives)
        {
            for (var split = end; split > start; split--)
            {
                var slice = text[start..split];
                if (Match(slice, 0, alternative, 0, options) == slice.Length
                    && MatchesRepeated(text, split, end, alternatives, options))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int FindGroupEnd(string pattern, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '\\':
                    i++;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    private static List<string> SplitAlternatives(string body)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < body.Length; i++)
        {
            switch (body[i])
            {
                case '\\':
                    i++;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case '|' when depth == 0:
                    result.Add(body[start..i]);
                    start = i + 1;
                    break;
            }
        }

        result.Add(body[start..]);
        return result;
    }

    private static bool CharsEqual(char a, char b, MatchOptions options) =>
        options.CaseInsensitive
            ? char.ToLowerInvariant(a) == char.ToLowerInvariant(b)
            : a == b;

    private static bool InRange(char c, char low, char high, MatchOptions options)
    {
        if (c >= low && c <= high)
        {
            return true;
        }

        if (!options.CaseInsensitive)
        {
            return false;
        }

        var lower = char.ToLowerInvariant(c);
        var upper = char.ToUpperInvariant(c);
        return (lower >= low && lower <= high) || (upper >= low && upper <= high);
    }

    private static bool MatchesClass(char c, string className) => className switch
    {
        "alpha" => char.IsLetter(c),
        "digit" => char.IsDigit(c),
        "alnum" => char.IsLetterOrDigit(c),
        "space" => char.IsWhiteSpace(c),
        "upper" => char.IsUpper(c),
        "lower" => char.IsLower(c),
        "punct" => char.IsPunctuation(c) || char.IsSymbol(c),
        "print" => !char.IsControl(c),
        "graph" => !char.IsControl(c) && !char.IsWhiteSpace(c),
        "cntrl" => char.IsControl(c),
        "xdigit" => Uri.IsHexDigit(c),
        "blank" => c is ' ' or '\t',
        "word" => char.IsLetterOrDigit(c) || c == '_',
        _ => false,
    };
}
