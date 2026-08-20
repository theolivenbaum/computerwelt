using System.Text;
using System.Text.RegularExpressions;
using Monty.Runtime;

namespace Monty.Modules;

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

        module.Add("compile", static arguments =>
        {
            Require("compile", arguments, 1, 2, "pattern", "flags");
            return new PyPattern(arguments[0].Display(), Options(arguments, 1));
        });

        module.Add("match", static arguments =>
        {
            Require("match", arguments, 2, 3, "pattern", "string", "flags");
            return Pattern(arguments).MatchAt(Subject(arguments), anchored: true);
        });

        module.Add("fullmatch", static arguments =>
        {
            Require("fullmatch", arguments, 2, 3, "pattern", "string", "flags");
            return Pattern(arguments).FullMatch(Subject(arguments));
        });

        module.Add("search", static arguments =>
        {
            Require("search", arguments, 2, 3, "pattern", "string", "flags");
            return Pattern(arguments).MatchAt(Subject(arguments), anchored: false);
        });

        module.Add("findall", static arguments =>
        {
            Require("findall", arguments, 2, 3, "pattern", "string", "flags");
            return Pattern(arguments).FindAll(Subject(arguments));
        });

        module.Add("finditer", static arguments =>
        {
            Require("finditer", arguments, 2, 3, "pattern", "string", "flags");
            return new PyIterator(VirtualMachine.RequireIterable(Pattern(arguments).FindIter(Subject(arguments))));
        });

        module.Add("sub", static arguments =>
        {
            Require("sub", arguments, 3, 5, "pattern", "repl", "string", "count", "flags");

            // The pattern's own flags argument is the fifth here, not the third.
            var pattern = arguments[0] is PyPattern compiled
                ? compiled
                : new PyPattern(arguments[0].Display(), Options(arguments, 4));

            return new PyStr(pattern.Substitute(
                Text(arguments[1], "repl"),
                Text(arguments[2], "string"),
                arguments.Length > 3 ? Count(arguments[3]) : 0));
        });

        module.Add("split", static arguments =>
        {
            Require("split", arguments, 2, 4, "pattern", "string", "maxsplit", "flags");
            return Pattern(arguments).Split(Subject(arguments));
        });

        module.Add("escape", static arguments => new PyStr(Regex.Escape(arguments[0].Display())));

        return module;
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
        // Monty documents callable replacements as unsupported, and a non-string repl is
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

    private static PyPattern Pattern(PyObject[] arguments) =>
        arguments[0] is PyPattern compiled ? compiled : new PyPattern(arguments[0].Display(), Options(arguments, 2));

    private static string Subject(PyObject[] arguments) => Text(arguments[1], "string");

    private static RegexOptions Options(PyObject[] arguments, int index)
    {
        if (index >= arguments.Length || arguments[index] is not PyInt flags)
        {
            return RegexOptions.None;
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

        return options;
    }

    /// <summary>A compiled pattern.</summary>
    public sealed class PyPattern : PyObject
    {
        private readonly Regex _regex;

        /// <summary>Compiles <paramref name="pattern"/>.</summary>
        public PyPattern(string pattern, RegexOptions options)
        {
            Source = pattern;

            // CPython's flags always include re.UNICODE, which a str pattern implies.
            Flags = 32
                | (options.HasFlag(RegexOptions.IgnoreCase) ? 2 : 0)
                | (options.HasFlag(RegexOptions.Multiline) ? 8 : 0)
                | (options.HasFlag(RegexOptions.Singleline) ? 16 : 0)
                | (options.HasFlag(RegexOptions.IgnorePatternWhitespace) ? 64 : 0);

            try
            {
                _regex = new Regex(Translate(pattern), options, MatchTimeout);
            }
            catch (ArgumentException e)
            {
                throw new PyRaise(new PyException(
                    PyExceptionType.PatternError, $"bad character in pattern: {e.Message}"));
            }
        }

        /// <summary>The pattern text as written.</summary>
        public string Source { get; }

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

            return names.Count == 0
                ? $"re.compile({PyStr.Quote(Source)})"
                : $"re.compile({PyStr.Quote(Source)}, {string.Join('|', names)})";
        }

        /// <inheritdoc />
        public override PyObject? GetAttribute(string name) => name switch
        {
            "pattern" => new PyStr(Source),
            "flags" => new PyInt(Flags),
            "groups" => new PyInt(_regex.GetGroupNumbers().Length - 1),

            "match" => new PyBuiltinFunction("match", arguments => MatchAt(arguments[0].Display(), anchored: true)),
            "fullmatch" => new PyBuiltinFunction("fullmatch", arguments => FullMatch(arguments[0].Display())),
            "search" => new PyBuiltinFunction("search", arguments => MatchAt(arguments[0].Display(), anchored: false)),
            "findall" => new PyBuiltinFunction("findall", arguments => FindAll(arguments[0].Display())),
            "finditer" => new PyBuiltinFunction("finditer", arguments =>
                new PyIterator(VirtualMachine.RequireIterable(FindIter(arguments[0].Display())))),

            "split" => new PyBuiltinFunction("split", arguments => Split(arguments[0].Display())),

            "sub" => new PyBuiltinFunction("sub", arguments =>
            {
                Require("sub", arguments, 2, 3, "repl", "string", "count");

                return new PyStr(Substitute(
                    Text(arguments[0], "repl"),
                    Text(arguments[1], "string"),
                    arguments.Length > 2 ? Count(arguments[2]) : 0));
            }),

            _ => null,
        };

        /// <summary>Matches at the start (for <c>match</c>) or anywhere (for <c>search</c>).</summary>
        public PyObject MatchAt(string subject, bool anchored)
        {
            var match = anchored
                ? _regex.Match(subject) is { Success: true, Index: 0 } atStart ? atStart : Match.Empty
                : _regex.Match(subject);

            return match.Success ? new PyMatch(match, subject) : PyNone.Instance;
        }

        /// <summary>Matches the whole subject.</summary>
        public PyObject FullMatch(string subject)
        {
            var match = _regex.Match(subject);

            return match.Success && match.Index == 0 && match.Length == subject.Length
                ? new PyMatch(match, subject)
                : PyNone.Instance;
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
            new PyList([.. _regex.Matches(subject).Select(match => (PyObject)new PyMatch(match, subject))]);

        /// <summary>Splits the subject on the pattern.</summary>
        public PyObject Split(string subject) =>
            new PyList([.. _regex.Split(subject).Select(static piece => (PyObject)new PyStr(piece))]);

        /// <summary>Replaces matches. A count of 0 means "all", as in Python.</summary>
        /// <remarks>
        /// A count of zero means "every match"; a negative one means none at all, which
        /// .NET spells as zero rather than as -1.
        /// </remarks>
        public string Substitute(string replacement, string subject, int count) =>
            _regex.Replace(subject, TranslateReplacement(replacement), count == 0 ? -1 : Math.Max(0, count));

        /// <summary>
        /// Translates the Python-only constructs .NET spells differently.
        /// </summary>
        private static string Translate(string pattern) =>
            pattern.Replace("(?P<", "(?<", StringComparison.Ordinal)
                .Replace("(?P=", @"\k<", StringComparison.Ordinal)
                .Replace(@"\Z", @"\z", StringComparison.Ordinal);

        /// <summary>Python's replacement uses <c>\1</c> and <c>\g&lt;name&gt;</c>; .NET uses <c>$1</c>.</summary>
        private static string TranslateReplacement(string replacement)
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
                    builder.Append('$').Append(next);
                    continue;
                }

                if (next == 'g' && i + 1 < replacement.Length && replacement[i + 1] == '<')
                {
                    var close = replacement.IndexOf('>', i + 1);

                    if (close > 0)
                    {
                        builder.Append("${").Append(replacement[(i + 2)..close]).Append('}');
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
    public sealed class PyMatch(Match match, string subject) : PyObject
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

                return new PyTuple([.. match.Groups.Keys
                    .Where(static key => key != "0")
                    .Select(key => match.Groups[key].Success ? new PyStr(match.Groups[key].Value) : fallback)]);
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

                // Only named groups appear in a groupdict; .NET reports numbered ones
                // under their digits, so those are filtered out.
                foreach (var key in match.Groups.Keys.Where(static k => !IsNumeric(k)))
                {
                    dict.Set(
                        new PyStr(key),
                        match.Groups[key].Success ? new PyStr(match.Groups[key].Value) : fallback);
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

        private static bool IsNumeric(string key) =>
            int.TryParse(key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _);

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
            if (key is PyInt index)
            {
                var numbered = match.Groups[(int)index.Value];

                return numbered is null || (!numbered.Success && !match.Groups.Keys.Contains(numbered.Name))
                    ? throw new PyRaise(PyErrors.IndexError("no such group"))
                    : numbered;
            }

            var name = key.Display();

            return !IsNumeric(name) && match.Groups.ContainsKey(name)
                ? match.Groups[name]
                : throw new PyRaise(PyErrors.IndexError("no such group"));
        }

        /// <inheritdoc />
        /// <remarks><c>m[n]</c> is <c>m.group(n)</c>, for a number or a name alike.</remarks>
        public override PyObject GetItem(PyObject index) => Group(index);
    }
}
