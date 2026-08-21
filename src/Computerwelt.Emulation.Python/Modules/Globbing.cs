using System.Text;
using System.Text.RegularExpressions;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// Shell-style pattern matching, and the directory walk built on it.
/// </summary>
/// <remarks>
/// <para>
/// One engine behind <c>fnmatch</c>, <c>glob</c> and <c>Path.glob</c>, because CPython's
/// three agree on what a pattern means and differ only in what they match it against:
/// <c>fnmatch</c> matches a whole string with <c>*</c> crossing anything, while the two
/// path forms match one segment at a time with <c>*</c> stopping at a separator and
/// <c>**</c> spanning them.
/// </para>
/// <para>
/// None of this exists upstream — see <c>tests/monty-extensions/</c> for what that means
/// for the corpus.
/// </para>
/// </remarks>
public static class Globbing
{
    /// <summary>Compiled patterns, keyed by their source and segment-awareness.</summary>
    /// <remarks>
    /// A glob recompiles the same segment pattern once per directory it descends into, so
    /// the cache is what keeps a deep <c>**</c> walk from spending its whole instruction
    /// budget in the regex compiler. Bounded, because the keys come from the program.
    /// </remarks>
    private static readonly Dictionary<(string Pattern, bool Segment), Regex> Compiled = [];

    private const int CacheLimit = 256;

    /// <summary>The depth cap used when no host set one — the shell filesystem's own.</summary>
    public const int DefaultMaxDepth = 64;

    /// <summary>Whether <paramref name="text"/> matches <paramref name="pattern"/>.</summary>
    /// <param name="text">The string to test.</param>
    /// <param name="pattern">The shell-style pattern.</param>
    /// <param name="segment">
    /// True to stop <c>*</c> and <c>?</c> at <c>/</c>, which is what a path pattern means.
    /// </param>
    public static bool Matches(string text, string pattern, bool segment = false) =>
        Pattern(pattern, segment).IsMatch(text);

    /// <summary>Compiles a pattern, reusing an earlier compilation when there is one.</summary>
    public static Regex Pattern(string pattern, bool segment)
    {
        lock (Compiled)
        {
            if (Compiled.TryGetValue((pattern, segment), out var cached))
            {
                return cached;
            }

            // `\A` is added here rather than in `Translate`, because that function's output
            // is `fnmatch.translate`'s and CPython's is written for `re.match`, which
            // anchors the start for you. `IsMatch` does not — without this, `.*` would find
            // the `.py` at the end of `top.py` and every hidden-file pattern would be wrong.
            var built = new Regex(
                @"\A" + Translate(pattern, segment),
                RegexOptions.Singleline | RegexOptions.CultureInvariant);

            // A program that generates patterns in a loop must not grow this without bound;
            // dropping everything is cheaper than tracking use and no less correct.
            if (Compiled.Count >= CacheLimit)
            {
                Compiled.Clear();
            }

            Compiled[(pattern, segment)] = built;
            return built;
        }
    }

    /// <summary>
    /// Turns a shell pattern into the regular expression <c>fnmatch.translate</c> reports.
    /// </summary>
    /// <param name="pattern">The shell-style pattern.</param>
    /// <param name="segment">True to stop <c>*</c> and <c>?</c> at <c>/</c>.</param>
    public static string Translate(string pattern, bool segment = false)
    {
        var any = segment ? "[^/]" : ".";
        var result = new StringBuilder("(?s:");
        var index = 0;

        while (index < pattern.Length)
        {
            var character = pattern[index++];

            switch (character)
            {
                case '*':
                    // Runs of stars collapse to one, as CPython's translate does, so that
                    // `a**b` is `a*b` rather than a pattern with two greedy holes in it.
                    while (index < pattern.Length && pattern[index] == '*')
                    {
                        index++;
                    }

                    result.Append(any).Append('*');
                    break;

                case '?':
                    result.Append(any);
                    break;

                case '[':
                    index = CharacterClass(pattern, index, result);
                    break;

                default:
                    result.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }

        return result.Append(@")\z").ToString();
    }

    /// <summary>True when a pattern contains anything that has to be matched.</summary>
    /// <remarks>
    /// <c>glob</c> uses this to answer a literal path with a single existence check rather
    /// than a directory listing, which is what makes <c>glob('exact/name.txt')</c> cheap.
    /// </remarks>
    public static bool HasMagic(string pattern) =>
        pattern.AsSpan().IndexOfAny('*', '?', '[') >= 0;

    /// <summary>Quotes every special character in <paramref name="pattern"/>.</summary>
    public static string Escape(string pattern)
    {
        var result = new StringBuilder(pattern.Length);

        foreach (var character in pattern)
        {
            if (character is '*' or '?' or '[')
            {
                result.Append('[').Append(character).Append(']');
            }
            else
            {
                result.Append(character);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Expands a glob pattern against the filesystem.
    /// </summary>
    /// <param name="fileSystem">Where to look.</param>
    /// <param name="root">
    /// The directory a relative pattern is resolved against. Results keep the pattern's own
    /// shape: a relative pattern yields relative paths, an absolute one absolute paths.
    /// </param>
    /// <param name="pattern">The pattern, which may contain <c>/</c> and <c>**</c>.</param>
    /// <param name="recursive">Whether <c>**</c> spans directories rather than acting as <c>*</c>.</param>
    /// <param name="includeHidden">Whether a wildcard may match a name beginning with a dot.</param>
    /// <param name="maxDepth">
    /// How many path components deep a <c>**</c> may reach before the walk refuses to go
    /// further. Reaching it raises rather than truncating, because a glob that quietly
    /// stopped part-way would report a subset of the tree as though it were all of it.
    /// </param>
    /// <returns>Matching paths, sorted, directories marked by a trailing separator when the pattern ends in one.</returns>
    public static List<string> Expand(
        IPyFileSystem fileSystem,
        string root,
        string pattern,
        bool recursive,
        bool includeHidden,
        int maxDepth = DefaultMaxDepth)
    {
        if (pattern.Length == 0)
        {
            return [];
        }

        var absolute = pattern.StartsWith('/');

        // A pattern ending in `/` asks for directories only, and CPython keeps the slash on
        // what it returns.
        var directoriesOnly = pattern.EndsWith('/');
        var trimmed = pattern.TrimEnd('/');

        if (trimmed.Length == 0)
        {
            return fileSystem.IsDirectory("/") ? ["/"] : [];
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var start = absolute ? "/" : root;
        var matches = new List<string>();

        Descend(fileSystem, start, segments, 0, recursive, includeHidden, maxDepth, matches);

        var results = matches
            .Where(path => !directoriesOnly || fileSystem.IsDirectory(path))
            .Select(path => Present(path, start, absolute, directoriesOnly))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToList();

        return results;
    }

    /// <summary>Renders one match the way the pattern that found it was written.</summary>
    private static string Present(string path, string start, bool absolute, bool directoriesOnly)
    {
        var shown = absolute ? path : Relative(path, start);
        return directoriesOnly ? shown + "/" : shown;
    }

    /// <summary>Strips the search root from a match, leaving the relative path.</summary>
    private static string Relative(string path, string root)
    {
        var prefix = root.EndsWith('/') ? root : root + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }

    /// <summary>Matches one pattern segment against a directory, then recurses.</summary>
    private static void Descend(
        IPyFileSystem fileSystem,
        string directory,
        string[] segments,
        int index,
        bool recursive,
        bool includeHidden,
        int maxDepth,
        List<string> matches)
    {
        if (index == segments.Length)
        {
            matches.Add(directory);
            return;
        }

        var segment = segments[index];

        // `**` on its own stands for "this directory and every directory under it", so the
        // rest of the pattern is tried at each depth — including zero, which is why
        // `**/*.py` finds a file in the search root itself.
        if (recursive && segment == "**")
        {
            foreach (var candidate in SelfAndDescendants(fileSystem, directory, includeHidden, maxDepth))
            {
                Descend(fileSystem, candidate, segments, index + 1, recursive, includeHidden, maxDepth, matches);
            }

            return;
        }

        // A literal segment needs no listing, which also means a pattern naming a file
        // exactly does not require its parent to be readable as a directory listing.
        if (!HasMagic(segment))
        {
            var literal = Join(directory, segment);

            if (fileSystem.Exists(literal))
            {
                Descend(fileSystem, literal, segments, index + 1, recursive, includeHidden, maxDepth, matches);
            }

            return;
        }

        if (!fileSystem.IsDirectory(directory))
        {
            return;
        }

        // A wildcard does not match a leading dot unless the pattern asked for one, which
        // is why `*` skips `.gitignore` but `.*` finds it.
        var hidden = includeHidden || segment.StartsWith('.');
        var expression = Pattern(segment, segment: true);

        foreach (var name in Listing(fileSystem, directory))
        {
            if (!hidden && name.StartsWith('.'))
            {
                continue;
            }

            if (expression.IsMatch(name))
            {
                Descend(fileSystem, Join(directory, name), segments, index + 1, recursive, includeHidden, maxDepth, matches);
            }
        }
    }

    /// <summary>A directory and every directory beneath it, breadth first.</summary>
    /// <remarks>
    /// Iterative rather than recursive: a deep tree would otherwise spend the interpreter's
    /// recursion budget on the host's stack, and a <c>**</c> pattern is exactly the case
    /// where the tree is deep.
    /// </remarks>
    private static IEnumerable<string> SelfAndDescendants(
        IPyFileSystem fileSystem,
        string directory,
        bool includeHidden,
        int maxDepth)
    {
        var pending = new Queue<string>([directory]);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            yield return current;

            if (!fileSystem.IsDirectory(current))
            {
                continue;
            }

            // Checked before listing, so the error names the directory that was about to
            // be descended into rather than one of its children.
            EnsureDepth(current, maxDepth);

            foreach (var name in Listing(fileSystem, current))
            {
                if (!includeHidden && name.StartsWith('.'))
                {
                    continue;
                }

                var child = Join(current, name);

                if (fileSystem.IsDirectory(child))
                {
                    pending.Enqueue(child);
                }
            }
        }
    }

    /// <summary>
    /// Refuses to descend below the depth cap.
    /// </summary>
    /// <remarks>
    /// Over the shell's own filesystem this can never fire — that filesystem will not
    /// create a path this deep in the first place. It is here for a host that supplies
    /// storage this sandbox did not build, where a tree can be arbitrarily deep or, with
    /// links, unbounded, and an unguarded <c>**</c> would never finish.
    /// </remarks>
    internal static void EnsureDepth(string path, int maxDepth)
    {
        if (Depth(path) >= maxDepth)
        {
            throw new PyRaise(new PyException(
                PyExceptionType.OSError,
                $"[Errno 40] Too many levels of directories: '{path}' exceeds the depth limit of {maxDepth}"));
        }
    }

    /// <summary>How many components a path has.</summary>
    internal static int Depth(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Lists a directory in a stable order, treating an unreadable one as empty.</summary>
    /// <remarks>
    /// A glob reports what it found; it does not fail because one directory along the way
    /// could not be read. CPython's does the same.
    /// </remarks>
    internal static IReadOnlyList<string> Listing(IPyFileSystem fileSystem, string directory)
    {
        try
        {
            return [.. fileSystem.List(directory).OrderBy(static name => name, StringComparer.Ordinal)];
        }
        catch (PyRaise)
        {
            return [];
        }
    }

    /// <summary>Joins a directory and a name without collapsing anything.</summary>
    internal static string Join(string directory, string name) =>
        directory.Length == 0 ? name
        : directory.EndsWith('/') ? directory + name
        : directory + "/" + name;

    /// <summary>Compiles a bracket expression, returning the index just past its close.</summary>
    private static int CharacterClass(string pattern, int index, StringBuilder result)
    {
        var close = index;

        // A `]` immediately after the opening bracket — or after its negation — is a
        // literal, so the search for the real close starts past it.
        if (close < pattern.Length && pattern[close] is '!' or '^')
        {
            close++;
        }

        if (close < pattern.Length && pattern[close] == ']')
        {
            close++;
        }

        while (close < pattern.Length && pattern[close] != ']')
        {
            close++;
        }

        // An unclosed bracket is a literal `[`, not an error.
        if (close >= pattern.Length)
        {
            result.Append("\\[");
            return index;
        }

        var body = pattern[index..close];
        result.Append('[');

        if (body.StartsWith('!'))
        {
            result.Append('^').Append(Sanitized(body[1..]));
        }
        else
        {
            // A leading `^` is literal in a shell pattern and would negate in a regex.
            result.Append(body.StartsWith('^') ? "\\^" + Sanitized(body[1..]) : Sanitized(body));
        }

        result.Append(']');
        return close + 1;
    }

    /// <summary>Escapes what a regex character class treats specially and a shell one does not.</summary>
    private static string Sanitized(string body) =>
        body.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
}
