using System.Text;
using System.Text.RegularExpressions;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>The <c>re</c> module.</summary>
/// <remarks>
/// Backed by .NET regex, which is close enough to Python's dialect for the constructs
/// scripts use, with the differences that matter translated: Python's <c>(?P&lt;name&gt;)</c>
/// group syntax, its flag constants, and its <c>\\Z</c> end anchor.
/// Every match runs under a timeout, since the pattern comes from the script.
/// </remarks>
public static class ReModule
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create()
    {
        var module = new PyModuleObject("re");

        module.Add("NOFLAG", new PyInt(0));
        module.Add("IGNORECASE", new PyInt(2));
        module.Add("I", new PyInt(2));
        module.Add("MULTILINE", new PyInt(8));
        module.Add("M", new PyInt(8));
        module.Add("DOTALL", new PyInt(16));
        module.Add("S", new PyInt(16));
        module.Add("VERBOSE", new PyInt(64));
        module.Add("X", new PyInt(64));
        module.Add("ASCII", new PyInt(256));
        module.Add("A", new PyInt(256));

        // A str pattern is Unicode by definition, so this flag only documents the default.
        module.Add("UNICODE", new PyInt(32));
        module.Add("U", new PyInt(32));

        // The classes are exposed so a script can name them in `isinstance`.
        module.Add("Pattern", new PyType(
            "re.Pattern",
            static value => value is PyPattern,
            static (_, _) => throw new PyRaise(PyErrors.TypeError("cannot create 're.Pattern' instances"))));

        module.Add("Match", new PyType(
            "re.Match",
            static value => value is PyMatch,
            static (_, _) => throw new PyRaise(PyErrors.TypeError("cannot create 're.Match' instances"))));

        // CPython 3.13 renamed `re.error`; both names are the same class.
        module.Add("error", PyExceptionType.Registry["PatternError"]);
        module.Add("PatternError", PyExceptionType.Registry["PatternError"]);

        module.Add("compile", new PyBuiltinFunction("compile", static (arguments, keywords) =>
        {
            var bound = Bind("compile", arguments, keywords, 1, "pattern", "flags");

            // A compiled pattern comes straight back, and combining it with flags is an
            // error rather than a silent recompile.
            if (bound[0] is PyPattern compiled)
            {
                return bound.Length > 1
                    ? throw new PyRaise(PyErrors.ValueError(
                        "cannot process flags argument with a compiled pattern"))
                    : compiled;
            }

            return new PyPattern(
                bound[0] is PyStr source
                    ? source.Value
                    : throw new PyRaise(PyErrors.TypeError(
                        "first argument must be string or compiled pattern")),
                Options(bound, 1),
                Raw(bound, 1));
        }));

        module.Add("match", new PyBuiltinFunction("match", static (arguments, keywords) =>
        {
            var bound = Bind("match", arguments, keywords, 2, "pattern", "string", "flags");
            return Pattern(bound).MatchAt(Subject(bound), anchored: true);
        }));

        module.Add("fullmatch", new PyBuiltinFunction("fullmatch", static (arguments, keywords) =>
        {
            var bound = Bind("fullmatch", arguments, keywords, 2, "pattern", "string", "flags");
            return Pattern(bound).FullMatch(Subject(bound));
        }));

        module.Add("search", new PyBuiltinFunction("search", static (arguments, keywords) =>
        {
            var bound = Bind("search", arguments, keywords, 2, "pattern", "string", "flags");
            return Pattern(bound).MatchAt(Subject(bound), anchored: false);
        }));

        module.Add("findall", new PyBuiltinFunction("findall", static (arguments, keywords) =>
        {
            var bound = Bind("findall", arguments, keywords, 2, "pattern", "string", "flags");
            return Pattern(bound).FindAll(Subject(bound));
        }));

        module.Add("finditer", new PyBuiltinFunction("finditer", static (arguments, keywords) =>
        {
            var bound = Bind("finditer", arguments, keywords, 2, "pattern", "string", "flags");
            return new PyIterator(VirtualMachine.RequireIterable(Pattern(bound).FindIter(Subject(bound))));
        }));

        module.Add("sub", new PyBuiltinFunction("sub", static (arguments, keywords) =>
        {
            var bound = Bind("sub", arguments, keywords, 3, "pattern", "repl", "string", "count", "flags");

            // The pattern's own flags argument is the fifth here, not the third.
            var pattern = Pattern(bound, 4);

            return new PyStr(pattern.Substitute(
                Text(bound[1], "repl"),
                Text(bound[2], "string"),
                bound.Length > 3 ? Count(bound[3]) : 0));
        }));

        module.Add("split", new PyBuiltinFunction("split", static (arguments, keywords) =>
        {
            var bound = Bind("split", arguments, keywords, 2, "pattern", "string", "maxsplit", "flags");

            // split's flags are the fourth argument; the third is the split limit.
            return Pattern(bound, 3).Split(Subject(bound), bound.Length > 2 ? Count(bound[2]) : 0);
        }));

        module.Add("escape", new PyBuiltinFunction("escape", static (arguments, keywords) =>
        {
            if (arguments.Length > 1)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"escape() takes 1 positional argument but {arguments.Length} were given"));
            }

            var bound = Bind("escape", arguments, keywords, 1, "pattern");

            return new PyStr(Escape(bound[0] is PyStr text
                ? text.Value
                : throw new PyRaise(PyErrors.TypeError(
                    $"decoding to str: need a bytes-like object, {bound[0].TypeName} found"))));
        }));

        return module;
    }

    /// <summary>
    /// Binds a module function's arguments, which are positional-or-keyword because
    /// CPython writes these in Python rather than in C.
    /// </summary>
    private static PyObject[] Bind(
        string name, PyObject[] arguments, PyDict? keywords, int minimum, params string[] names)
    {
        var given = new PyObject?[names.Length];

        for (var i = 0; i < Math.Min(arguments.Length, names.Length); i++)
        {
            given[i] = arguments[i];
        }

        // A Python `def` binds its keywords before it counts its positionals, so an unknown
        // or duplicated keyword is reported even when there are surplus positionals too.
        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            var position = Array.IndexOf(names, key.Display());

            if (position < 0)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{name}() got an unexpected keyword argument '{key.Display()}'"));
            }

            if (given[position] is not null)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{name}() got multiple values for argument '{names[position]}'"));
            }

            given[position] = value;
        }

        if (arguments.Length > names.Length)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{name}() takes from {minimum} to {names.Length} positional arguments "
                + $"but {arguments.Length} were given"));
        }

        // Only a leading run of supplied arguments is usable; a gap means the caller left
        // an earlier parameter out, which the arity check then reports.
        PyObject[] bound = [.. given.TakeWhile(static value => value is not null).Select(static value => value!)];
        Require(name, bound, minimum, names.Length, names);

        return bound;
    }

    /// <summary>
    /// Enforces a module function's arity, naming every missing argument at once and
    /// spelling the maximum as a range — the way a Python <c>def</c> would.
    /// </summary>
    private static void Require(string name, PyObject[] arguments, int minimum, int maximum, params string[] names)
    {
        if (arguments.Length < minimum)
        {
            var missing = names.Skip(arguments.Length).Take(minimum - arguments.Length).ToList();

            throw new PyRaise(PyErrors.TypeError(
                $"{name}() missing {missing.Count} required positional "
                + $"argument{(missing.Count == 1 ? string.Empty : "s")}: "
                + (missing.Count == 1
                    ? $"'{missing[0]}'"
                    : string.Join(", ", missing.Take(missing.Count - 1).Select(m => $"'{m}'"))
                        + (missing.Count == 2 ? " and " : ", and ") + $"'{missing[^1]}'")));
        }

        if (arguments.Length > maximum)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{name}() takes at most {maximum} arguments ({arguments.Length} given)"));
        }
    }

    /// <summary>
    /// Binds a pattern method's arguments, which are positional-or-keyword but count
    /// their total the way a C-level parser does.
    /// </summary>
    private static PyObject[] Method(
        string name, PyObject[] arguments, PyDict? keywords, int minimum, params string[] names)
    {
        var total = arguments.Length + (keywords?.Count ?? 0);

        if (total > names.Length)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{name}() takes at most {names.Length} arguments ({total} given)"));
        }

        var given = new PyObject?[names.Length];

        for (var i = 0; i < arguments.Length; i++)
        {
            given[i] = arguments[i];
        }

        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            var position = Array.IndexOf(names, key.Display());

            if (position < 0)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{name}() got an unexpected keyword argument '{key.Display()}'"));
            }

            given[position] = value;
        }

        PyObject[] bound = [.. given.TakeWhile(static value => value is not null).Select(static value => value!)];
        Positional(name, bound, minimum, names.Length, names);

        return bound;
    }

    /// <summary>
    /// Enforces a method's arity, naming the first missing argument and its position.
    /// </summary>
    private static void Positional(string name, PyObject[] arguments, int minimum, int maximum, params string[] names)
    {
        if (arguments.Length < minimum)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{name}() missing required argument '{names[arguments.Length]}' (pos {arguments.Length + 1})"));
        }

        if (arguments.Length > maximum)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{name}() takes from {minimum} to {maximum} positional arguments "
                + $"but {arguments.Length} were given"));
        }
    }

    /// <summary>
    /// Reads a string argument, which CPython type-checks before it starts matching — so
    /// even a substitution that will replace nothing rejects a non-string.
    /// </summary>
    private static string Text(PyObject value, string what) => value switch
    {
        PyStr text => text.Value,
        // Upstream documents callable replacements as unsupported, and a non-string repl is
        // reported that way rather than as a decoding failure.
        _ when what == "repl" => throw new PyRaise(PyErrors.TypeError(
            "callable replacement is not yet supported in re.sub()")),
        _ => throw new PyRaise(PyErrors.TypeError(
            $"expected string or bytes-like object, got '{value.TypeName}'")),
    };

    /// <summary>Reads a replacement count, clamping one larger than any real subject.</summary>
    private static int Count(PyObject value)
    {
        if (value is not PyInt count)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"'{value.TypeName}' object cannot be interpreted as an integer"));
        }

        return count.Value > int.MaxValue ? int.MaxValue
            : count.Value < int.MinValue ? int.MinValue
            : (int)count.Value;
    }

    private static PyPattern Pattern(PyObject[] arguments) => Pattern(arguments, 2);

    private static PyPattern Pattern(PyObject[] arguments, int flagIndex) => arguments[0] switch
    {
        PyPattern compiled when arguments.Length > flagIndex =>
            throw new PyRaise(PyErrors.ValueError("cannot process flags argument with a compiled pattern")),
        PyPattern compiled => compiled,
        PyStr source => new PyPattern(source.Value, Options(arguments, flagIndex), Raw(arguments, flagIndex)),
        _ => throw new PyRaise(PyErrors.TypeError("first argument must be string or compiled pattern")),
    };

    private static string Subject(PyObject[] arguments) => Text(arguments[1], "string");

    /// <summary>
    /// Escapes the characters Python's <c>re.escape</c> escapes.
    /// </summary>
    /// <remarks>
    /// Not <c>Regex.Escape</c>: .NET leaves <c>]</c> and <c>}</c> alone and escapes
    /// whitespace as <c>\n</c>-style sequences, while Python backslashes every character
    /// that is special anywhere.
    /// </remarks>
    private static string Escape(string text)
    {
        const string Special = "()[]{}?*+-|^$\\.&~# \t\n\r\v\f";
        var builder = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (Special.Contains(c, StringComparison.Ordinal))
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>The flag bits as given, including any with no .NET equivalent.</summary>
    private static int Raw(PyObject[] arguments, int index) =>
        index < arguments.Length && arguments[index] is PyInt flags ? (int)flags.Value : 0;

    private static RegexOptions Options(PyObject[] arguments, int index)
    {
        if (index >= arguments.Length)
        {
            return RegexOptions.None;
        }

        // CPython masks the flags with an int, so a non-integer is reported by the `&`
        // that fails rather than by a bespoke check.
        if (arguments[index] is not PyInt flags)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"unsupported operand type(s) for &: '{arguments[index].TypeName}' and 'int'"));
        }

        var options = RegexOptions.None;
        var value = (int)flags.Value;

        if ((value & 2) != 0)
        {
            options |= RegexOptions.IgnoreCase;
        }

        if ((value & 8) != 0)
        {
            options |= RegexOptions.Multiline;
        }

        if ((value & 16) != 0)
        {
            options |= RegexOptions.Singleline;
        }

        if ((value & 64) != 0)
        {
            options |= RegexOptions.IgnorePatternWhitespace;
        }

        // `re.ASCII` narrows the character classes to ASCII, which .NET spells as the
        // ECMAScript matching behaviour.
        if ((value & 256) != 0)
        {
            options |= RegexOptions.ECMAScript;
        }

        return options;
    }

    /// <summary>A compiled pattern.</summary>
    public sealed class PyPattern : PyObject
    {
        private readonly Regex _regex;
        private readonly Dictionary<string, int> _names = new(StringComparer.Ordinal);
        private readonly int _count;

        /// <summary>Compiles <paramref name="pattern"/>.</summary>
        public PyPattern(string pattern, RegexOptions options, int raw = 0)
        {
            Source = pattern;

            // CPython's flags always include re.UNICODE, which a str pattern implies —
            // unless re.ASCII narrowed the character classes. Bits with no .NET equivalent
            // are carried through from the argument, because `flags` reports them.
            Flags = raw
                | (options.HasFlag(RegexOptions.ECMAScript) ? 256 : 32)
                | (options.HasFlag(RegexOptions.IgnoreCase) ? 2 : 0)
                | (options.HasFlag(RegexOptions.Multiline) ? 8 : 0)
                | (options.HasFlag(RegexOptions.Singleline) ? 16 : 0)
                | (options.HasFlag(RegexOptions.IgnorePatternWhitespace) ? 64 : 0);

            try
            {
                _regex = new Regex(Translate(pattern, _names, out _count), options, MatchTimeout);
            }
            catch (ArgumentException e)
            {
                throw new PyRaise(new PyException(
                    PyExceptionType.PatternError, $"bad character in pattern: {e.Message}"));
            }
        }

        /// <summary>The pattern text as written.</summary>
        public string Source { get; }

        /// <summary>The named groups, mapped to their numbers.</summary>
        public IReadOnlyDictionary<string, int> Names => _names;

        /// <summary>How many capturing groups the pattern has.</summary>
        public int GroupCount => _count;

        private PyDict GroupIndex()
        {
            var index = new PyDict();

            foreach (var (name, number) in _names)
            {
                index.Set(new PyStr(name), new PyInt(number));
            }

            return index;
        }

        /// <summary>The flag bits, as CPython reports them.</summary>
        public int Flags { get; }

        /// <inheritdoc />
        public override string TypeName => "re.Pattern";

        /// <inheritdoc />
        /// <remarks>
        /// Two patterns compiled from the same source and flags are equal, which is what
        /// makes the compile cache invisible.
        /// </remarks>
        public override bool PyEquals(PyObject other) =>
            other is PyPattern pattern
            && string.Equals(Source, pattern.Source, StringComparison.Ordinal)
            && Flags == pattern.Flags;

        /// <inheritdoc />
        public override string Repr()
        {
            // The flags are listed in CPython's declaration order, joined by `|`.
            var names = new List<string>();

            if ((Flags & 2) != 0)
            {
                names.Add("re.IGNORECASE");
            }

            if ((Flags & 8) != 0)
            {
                names.Add("re.MULTILINE");
            }

            if ((Flags & 16) != 0)
            {
                names.Add("re.DOTALL");
            }

            if ((Flags & 64) != 0)
            {
                names.Add("re.VERBOSE");
            }

            if ((Flags & 256) != 0)
            {
                names.Add("re.ASCII");
            }

            return names.Count == 0
                ? $"re.compile({PyStr.Quote(Source)})"
                : $"re.compile({PyStr.Quote(Source)}, {string.Join('|', names)})";
        }

        /// <inheritdoc />
        public override PyObject? GetAttribute(string name) => name switch
        {
            "pattern" => new PyStr(Source),
            "flags" => new PyInt(Flags),
            "groups" => new PyInt(_count),
            "groupindex" => GroupIndex(),

            "match" => new PyBuiltinFunction("match", arguments => MatchAt(arguments[0].Display(), anchored: true)),
            "fullmatch" => new PyBuiltinFunction("fullmatch", arguments => FullMatch(arguments[0].Display())),
            "search" => new PyBuiltinFunction("search", arguments => MatchAt(arguments[0].Display(), anchored: false)),
            "findall" => new PyBuiltinFunction("findall", arguments => FindAll(arguments[0].Display())),
            "finditer" => new PyBuiltinFunction("finditer", arguments =>
                new PyIterator(VirtualMachine.RequireIterable(FindIter(arguments[0].Display())))),

            "split" => new PyBuiltinFunction("split", (arguments, keywords) =>
            {
                var bound = Method("split", arguments, keywords, 1, "string", "maxsplit");
                return Split(Text(bound[0], "string"), bound.Length > 1 ? Count(bound[1]) : 0);
            }),

            "sub" => new PyBuiltinFunction("sub", (arguments, keywords) =>
            {
                var bound = Method("sub", arguments, keywords, 2, "repl", "string", "count");

                return new PyStr(Substitute(
                    Text(bound[0], "repl"),
                    Text(bound[1], "string"),
                    bound.Length > 2 ? Count(bound[2]) : 0));
            }),

            _ => null,
        };

        /// <summary>Matches at the start (for <c>match</c>) or anywhere (for <c>search</c>).</summary>
        public PyObject MatchAt(string subject, bool anchored)
        {
            var match = anchored
                ? _regex.Match(subject) is { Success: true, Index: 0 } atStart ? atStart : Match.Empty
                : _regex.Match(subject);

            return match.Success ? new PyMatch(match, subject, this) : PyNone.Instance;
        }

        /// <summary>Matches the whole subject.</summary>
        /// <remarks>
        /// The whole subject must match, so the pattern is anchored at both ends rather
        /// than matched and then measured: `a|ab` against `ab` has to try the second
        /// alternative, which a plain match would never reach.
        /// </remarks>
        public PyObject FullMatch(string subject)
        {
            var anchored = new Regex(
                @"\A(?:" + Translate(Source, new Dictionary<string, int>(StringComparer.Ordinal), out _) + @")\z",
                _regex.Options,
                MatchTimeout);
            var match = anchored.Match(subject);

            return match.Success ? new PyMatch(match, subject, this) : PyNone.Instance;
        }

        /// <summary>
        /// Returns the matched text, or the single group when the pattern has one, or a
        /// tuple of groups when it has several — which is what <c>findall</c> does.
        /// </summary>
        public PyObject FindAll(string subject)
        {
            var results = new List<PyObject>();

            foreach (Match match in _regex.Matches(subject))
            {
                var groups = _regex.GetGroupNumbers().Where(static n => n > 0).ToList();

                results.Add(groups.Count switch
                {
                    0 => new PyStr(match.Value),
                    1 => new PyStr(match.Groups[groups[0]].Value),
                    _ => new PyTuple([.. groups.Select(n => (PyObject)new PyStr(match.Groups[n].Value))]),
                });
            }

            return new PyList(results);
        }

        /// <summary>Every match, as match objects.</summary>
        public PyObject FindIter(string subject) =>
            new PyList([.. _regex.Matches(subject).Select(match => (PyObject)new PyMatch(match, subject, this))]);

        /// <summary>Splits the subject on the pattern.</summary>
        /// <remarks>
        /// A <c>maxsplit</c> of zero means "no limit"; a negative one means no splitting at
        /// all, so the subject comes back whole.
        /// </remarks>
        public PyObject Split(string subject, int maxsplit) =>
            maxsplit < 0
                ? new PyList([new PyStr(subject)])
                : new PyList([.. _regex
                    .Split(subject, maxsplit == 0 ? int.MaxValue : maxsplit + 1)
                    .Select(static piece => (PyObject)new PyStr(piece))]);

        /// <summary>Replaces matches. A count of 0 means "all", as in Python.</summary>
        /// <remarks>
        /// A count of zero means "every match"; a negative one means none at all, which
        /// .NET spells as zero rather than as -1.
        /// </remarks>
        public string Substitute(string replacement, string subject, int count) =>
            _regex.Replace(subject, TranslateReplacement(replacement, _names), count == 0 ? -1 : Math.Max(0, count));

        /// <summary>
        /// Translates the Python-only constructs .NET spells differently, and gives every
        /// capturing group an explicit name.
        /// </summary>
        /// <remarks>
        /// The naming is what keeps the numbering Python's: .NET numbers the unnamed groups
        /// first and the named ones after, so a pattern mixing the two would renumber, and
        /// <c>\2</c> would point at a different group than the script wrote.
        /// </remarks>
        private static string Translate(string pattern, Dictionary<string, int> names, out int count)
        {
            var builder = new StringBuilder(pattern.Length);
            var group = 0;
            var inClass = false;
            count = 0;

            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];

                if (c == '\\' && i + 1 < pattern.Length)
                {
                    // `\Z` is `\z` in .NET; every other escape passes through.
                    builder.Append(c).Append(pattern[i + 1] == 'Z' ? 'z' : pattern[++i]);

                    if (pattern[i] == 'Z')
                    {
                        i++;
                    }

                    continue;
                }

                if (inClass)
                {
                    inClass = c != ']';
                    builder.Append(c);
                    continue;
                }

                if (c == '[')
                {
                    inClass = true;
                    builder.Append(c);
                    continue;
                }

                if (c != '(')
                {
                    builder.Append(c);
                    continue;
                }

                // `(?P<name>` names a group, `(?P=name)` back-references one, and every
                // other `(?` form is a non-capturing construct that keeps its spelling.
                if (pattern.AsSpan(i).StartsWith("(?P<", StringComparison.Ordinal))
                {
                    var close = pattern.IndexOf('>', i);
                    names[pattern[(i + 4)..close]] = ++group;
                    builder.Append("(?<g").Append(group).Append('>');
                    i = close;
                    continue;
                }

                if (pattern.AsSpan(i).StartsWith("(?P=", StringComparison.Ordinal))
                {
                    var close = pattern.IndexOf(')', i);
                    builder.Append("\\k<g").Append(names[pattern[(i + 4)..close]]).Append('>');
                    i = close;
                    continue;
                }

                if (i + 1 < pattern.Length && pattern[i + 1] == '?')
                {
                    builder.Append(c);
                    continue;
                }

                builder.Append("(?<g").Append(++group).Append('>');
            }

            count = group;
            return builder.ToString();
        }

        /// <summary>Python's replacement uses <c>\1</c> and <c>\g&lt;name&gt;</c>; .NET uses <c>$1</c>.</summary>
        private static string TranslateReplacement(string replacement, IReadOnlyDictionary<string, int> names)
        {
            var builder = new StringBuilder(replacement.Length);

            for (var i = 0; i < replacement.Length; i++)
            {
                if (replacement[i] == '$')
                {
                    builder.Append("$$");
                    continue;
                }

                if (replacement[i] != '\\' || i + 1 >= replacement.Length)
                {
                    builder.Append(replacement[i]);
                    continue;
                }

                var next = replacement[++i];

                if (char.IsAsciiDigit(next))
                {
                    // The groups were renamed to keep Python's numbering, so a numeric
                    // back-reference names one rather than indexing .NET's order.
                    var digits = next.ToString();

                    while (i + 1 < replacement.Length && char.IsAsciiDigit(replacement[i + 1]))
                    {
                        digits += replacement[++i];
                    }

                    // Group zero is the whole match, which .NET spells `$0`.
                    builder.Append(digits.TrimStart('0').Length == 0 ? "$0" : "${g" + digits + "}");
                    continue;
                }

                if (next == 'g' && i + 1 < replacement.Length && replacement[i + 1] == '<')
                {
                    var close = replacement.IndexOf('>', i + 1);

                    if (close > 0)
                    {
                        var reference = replacement[(i + 2)..close];

                        var target = names.TryGetValue(reference, out var number)
                            ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            : reference;

                        // Group zero is the whole match, which .NET spells `$0`.
                        builder.Append(target == "0" ? "$0" : "${g" + target + "}");

                        i = close;
                        continue;
                    }
                }

                builder.Append(next switch
                {
                    'n' => "\n",
                    't' => "\t",
                    'r' => "\r",
                    '\\' => "\\",
                    _ => "\\" + next,
                });
            }

            return builder.ToString();
        }
    }

    /// <summary>A match object.</summary>
    public sealed class PyMatch(Match match, string subject, PyPattern pattern) : PyObject
    {
        /// <inheritdoc />
        public override string TypeName => "re.Match";

        /// <inheritdoc />
        public override string Repr() =>
            $"<re.Match object; span=({match.Index}, {match.Index + match.Length}), match={PyStr.Quote(match.Value)}>";

        /// <inheritdoc />
        public override bool IsTruthy() => true;

        /// <inheritdoc />
        public override PyObject? GetAttribute(string name) => name switch
        {
            "string" => new PyStr(subject),

            "group" => new PyBuiltinFunction("group", arguments => arguments.Length switch
            {
                0 => Group(new PyInt(0)),
                1 => Group(arguments[0]),
                _ => new PyTuple([.. arguments.Select(Group)]),
            }),

            "groups" => new PyBuiltinFunction("groups", arguments =>
            {
                var fallback = arguments.Length > 0 ? arguments[0] : PyNone.Instance;

                // The groups come back in Python's order, which is the order they were
                // written in, and an unparticipating one takes the default.
                return new PyTuple([.. Enumerable.Range(1, pattern.GroupCount)
                    .Select(number => match.Groups["g" + number] is { Success: true } group
                        ? new PyStr(group.Value)
                        : fallback)]);
            }),

            "groupdict" => new PyBuiltinFunction("groupdict", (arguments, keywords) =>
            {
                if (arguments.Length + (keywords?.Count ?? 0) > 1)
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"groupdict() takes at most 1 argument ({arguments.Length + (keywords?.Count ?? 0)} given)"));
                }

                foreach (var (key, _) in keywords?.Entries ?? [])
                {
                    if (key.Display() != "default")
                    {
                        throw new PyRaise(PyErrors.TypeError(
                            $"groupdict() got an unexpected keyword argument '{key.Display()}'"));
                    }
                }

                var fallback = arguments.Length > 0 ? arguments[0]
                    : keywords?.TryGetValue(new PyStr("default"), out var value) == true ? value
                    : PyNone.Instance;

                var dict = new PyDict();

                foreach (var (name, number) in pattern.Names)
                {
                    dict.Set(
                        new PyStr(name),
                        match.Groups["g" + number] is { Success: true } group
                            ? new PyStr(group.Value)
                            : fallback);
                }

                return dict;
            }),

            // A group that did not participate spans (-1, -1) rather than reporting the
            // zero-length position .NET leaves it at.
            "start" => new PyBuiltinFunction("start", arguments =>
                new PyInt(Bounds(GroupOf(arguments)).Start)),

            "end" => new PyBuiltinFunction("end", arguments =>
                new PyInt(Bounds(GroupOf(arguments)).End)),

            "span" => new PyBuiltinFunction("span", arguments =>
            {
                var (start, end) = Bounds(GroupOf(arguments));
                return new PyTuple([new PyInt(start), new PyInt(end)]);
            }),

            _ => null,
        };

        private Group GroupOf(PyObject[] arguments) =>
            arguments.Length == 0 ? match : Resolve(arguments[0]);

        private static (int Start, int End) Bounds(Group group) =>
            group.Success ? (group.Index, group.Index + group.Length) : (-1, -1);

        private PyObject Group(PyObject key)
        {
            var group = Resolve(key);
            return group.Success ? new PyStr(group.Value) : PyNone.Instance;
        }

        private Group Resolve(PyObject key)
        {
            // A string key names a group; it is never read as a number, so `m.group('1')`
            // is a missing name rather than group one.
            var number = key switch
            {
                PyInt index when index.Value >= 0 && index.Value <= pattern.GroupCount => (int)index.Value,
                PyStr name when pattern.Names.TryGetValue(name.Value, out var found) => found,
                _ => throw new PyRaise(PyErrors.IndexError("no such group")),
            };

            return number == 0 ? match : match.Groups["g" + number];
        }

        /// <inheritdoc />
        /// <remarks><c>m[n]</c> is <c>m.group(n)</c>, for a number or a name alike.</remarks>
        public override PyObject GetItem(PyObject index) => Group(index);
    }
}
