using System.Globalization;
using System.Text;

namespace Computerwelt.Emulation.Bash.Interpreter;

/// <summary>
/// Expands brace constructs: <c>{a,b}</c> lists and <c>{1..5}</c> / <c>{a..e}</c> ranges.
/// </summary>
/// <remarks>
/// Brace expansion happens first, before any other expansion, and is purely textual — it
/// knows nothing about variables or files. It is also the one expansion that can multiply
/// a word into arbitrarily many, so it charges against the execution budget.
/// </remarks>
public static class BraceExpander
{
    /// <summary>Expands <paramref name="text"/> into every combination it denotes.</summary>
    public static List<string> Expand(string text, ExecutionLimits? limits = null)
    {
        limits ??= ExecutionLimits.Default;
        var results = new List<string>();
        ExpandInto(text, results, limits);
        return results;
    }

    private static void ExpandInto(string text, List<string> results, ExecutionLimits limits)
    {
        if (results.Count > limits.MaxBraceExpansionItems)
        {
            throw new LimitExceededException("max_brace_expansion_items", limits.MaxBraceExpansionItems);
        }

        var open = FindOpeningBrace(text);
        if (open < 0)
        {
            results.Add(text);
            return;
        }

        var close = FindMatchingBrace(text, open);
        if (close < 0)
        {
            results.Add(text);
            return;
        }

        var prefix = text[..open];
        var body = text[(open + 1)..close];
        var suffix = text[(close + 1)..];

        if (TryExpandRange(body) is { } range)
        {
            foreach (var item in range)
            {
                ExpandInto(prefix + item + suffix, results, limits);
            }

            return;
        }

        var alternatives = SplitAlternatives(body);

        // A brace group with no top-level comma is literal: `{a}` stays `{a}`. The braces
        // are kept, but the interior and the suffix may still contain real groups — and
        // re-expanding the reconstructed string would find this same group again, so the
        // pieces are expanded separately and recombined.
        if (alternatives.Count < 2)
        {
            var bodies = new List<string>();
            ExpandInto(body, bodies, limits);

            var suffixes = new List<string>();
            ExpandInto(suffix, suffixes, limits);

            foreach (var expandedBody in bodies)
            {
                foreach (var expandedSuffix in suffixes)
                {
                    results.Add(prefix + "{" + expandedBody + "}" + expandedSuffix);
                }
            }

            return;
        }

        foreach (var alternative in alternatives)
        {
            ExpandInto(prefix + alternative + suffix, results, limits);
        }
    }

    /// <summary>Finds the first brace that could open an expansion, skipping quoted text.</summary>
    private static int FindOpeningBrace(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c is '\'' or '"')
            {
                var end = text.IndexOf(c, i + 1);
                i = end < 0 ? text.Length : end;
                continue;
            }

            // `${...}` is a parameter expansion, not a brace group.
            if (c == '$' && i + 1 < text.Length && text[i + 1] == '{')
            {
                var end = FindMatchingBrace(text, i + 1);
                i = end < 0 ? text.Length : end;
                continue;
            }

            if (c == '{')
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindMatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c is '\'' or '"')
            {
                var end = text.IndexOf(c, i + 1);
                i = end < 0 ? text.Length : end;
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
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

    private static List<string> SplitAlternatives(string body)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];

            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c is '\'' or '"')
            {
                var end = body.IndexOf(c, i + 1);
                i = end < 0 ? body.Length : end;
                continue;
            }

            switch (c)
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    result.Add(body[start..i]);
                    start = i + 1;
                    break;
            }
        }

        result.Add(body[start..]);
        return result;
    }

    /// <summary>
    /// Expands <c>{start..end}</c> or <c>{start..end..step}</c> for integers and single
    /// characters. Returns <see langword="null"/> when the body is not a range.
    /// </summary>
    private static List<string>? TryExpandRange(string body)
    {
        var parts = body.Split("..");
        if (parts.Length is not (2 or 3))
        {
            return null;
        }

        var stepText = parts.Length == 3 ? parts[2] : null;

        if (TryParseLong(parts[0], out var from) && TryParseLong(parts[1], out var to))
        {
            long step = 1;
            if (stepText is not null && (!TryParseLong(stepText, out step) || step == 0))
            {
                return null;
            }

            step = Math.Abs(step);
            if (step == 0)
            {
                return null;
            }

            // Zero-padded endpoints make the whole sequence zero-padded to the same width.
            var width = 0;
            if (IsZeroPadded(parts[0]) || IsZeroPadded(parts[1]))
            {
                width = Math.Max(parts[0].TrimStart('-').Length, parts[1].TrimStart('-').Length);
            }

            var values = new List<string>();
            if (from <= to)
            {
                for (var v = from; v <= to; v += step)
                {
                    values.Add(Format(v, width));
                }
            }
            else
            {
                for (var v = from; v >= to; v -= step)
                {
                    values.Add(Format(v, width));
                }
            }

            return values;
        }

        if (parts[0].Length == 1 && parts[1].Length == 1
            && char.IsAsciiLetter(parts[0][0]) && char.IsAsciiLetter(parts[1][0]))
        {
            var step = 1;
            if (stepText is not null && (!int.TryParse(stepText, CultureInfo.InvariantCulture, out step) || step == 0))
            {
                return null;
            }

            step = Math.Abs(step);
            var start = parts[0][0];
            var end = parts[1][0];
            var values = new List<string>();

            if (start <= end)
            {
                for (var c = start; c <= end; c = (char)(c + step))
                {
                    values.Add(c.ToString());
                }
            }
            else
            {
                for (var c = start; c >= end; c = (char)(c - step))
                {
                    values.Add(c.ToString());
                }
            }

            return values;
        }

        return null;
    }

    private static bool TryParseLong(string text, out long value) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    private static bool IsZeroPadded(string text)
    {
        var digits = text.TrimStart('-');
        return digits.Length > 1 && digits[0] == '0';
    }

    private static string Format(long value, int width)
    {
        if (width <= 0)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        var negative = value < 0;
        var digits = Math.Abs(value).ToString(CultureInfo.InvariantCulture).PadLeft(negative ? width - 1 : width, '0');
        return negative ? "-" + digits : digits;
    }

    /// <summary>Removes the backslashes that protected braces and commas from expansion.</summary>
    internal static string Unescape(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '{' or '}' or ',')
            {
                builder.Append(text[++i]);
                continue;
            }

            builder.Append(text[i]);
        }

        return builder.ToString();
    }
}
