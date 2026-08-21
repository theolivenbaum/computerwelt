using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Computerwelt.Emulation.Bash.Builtins.Awk;

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
    private static string Translate(string pattern) => PosixRegex.TranslateExtended(pattern);
}
