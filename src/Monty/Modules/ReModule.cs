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

        module.Add("IGNORECASE", new PyInt(2));
        module.Add("I", new PyInt(2));
        module.Add("MULTILINE", new PyInt(8));
        module.Add("M", new PyInt(8));
        module.Add("DOTALL", new PyInt(16));
        module.Add("S", new PyInt(16));
        module.Add("VERBOSE", new PyInt(64));
        module.Add("X", new PyInt(64));

        module.Add("compile", static arguments => new PyPattern(
            arguments[0].Display(), Options(arguments, 1)));

        module.Add("match", static arguments =>
            Pattern(arguments).MatchAt(Subject(arguments), anchored: true));

        module.Add("fullmatch", static arguments =>
            Pattern(arguments).FullMatch(Subject(arguments)));

        module.Add("search", static arguments =>
            Pattern(arguments).MatchAt(Subject(arguments), anchored: false));

        module.Add("findall", static arguments => Pattern(arguments).FindAll(Subject(arguments)));

        module.Add("finditer", static arguments =>
            new PyIterator(VirtualMachine.RequireIterable(Pattern(arguments).FindIter(Subject(arguments)))));

        module.Add("sub", static arguments => new PyStr(Pattern(arguments).Substitute(
            arguments[1].Display(),
            arguments[2].Display(),
            arguments.Length > 3 && arguments[3] is PyInt count ? (int)count.Value : 0)));

        module.Add("split", static arguments => Pattern(arguments).Split(Subject(arguments)));

        module.Add("escape", static arguments => new PyStr(Regex.Escape(arguments[0].Display())));

        return module;
    }

    private static PyPattern Pattern(PyObject[] arguments) =>
        arguments[0] is PyPattern compiled ? compiled : new PyPattern(arguments[0].Display(), Options(arguments, 2));

    private static string Subject(PyObject[] arguments) => arguments[1].Display();

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

            try
            {
                _regex = new Regex(Translate(pattern), options, MatchTimeout);
            }
            catch (ArgumentException e)
            {
                throw new PyRaise(PyErrors.Create("ValueError", $"bad character in pattern: {e.Message}"));
            }
        }

        /// <summary>The pattern text as written.</summary>
        public string Source { get; }

        /// <inheritdoc />
        public override string TypeName => "Pattern";

        /// <inheritdoc />
        public override string Repr() => $"re.compile({PyStr.Quote(Source)})";

        /// <inheritdoc />
        public override PyObject? GetAttribute(string name) => name switch
        {
            "pattern" => new PyStr(Source),

            "match" => new PyBuiltinFunction("match", arguments => MatchAt(arguments[0].Display(), anchored: true)),
            "fullmatch" => new PyBuiltinFunction("fullmatch", arguments => FullMatch(arguments[0].Display())),
            "search" => new PyBuiltinFunction("search", arguments => MatchAt(arguments[0].Display(), anchored: false)),
            "findall" => new PyBuiltinFunction("findall", arguments => FindAll(arguments[0].Display())),
            "finditer" => new PyBuiltinFunction("finditer", arguments =>
                new PyIterator(VirtualMachine.RequireIterable(FindIter(arguments[0].Display())))),

            "split" => new PyBuiltinFunction("split", arguments => Split(arguments[0].Display())),

            "sub" => new PyBuiltinFunction("sub", arguments => new PyStr(Substitute(
                arguments[0].Display(),
                arguments[1].Display(),
                arguments.Length > 2 && arguments[2] is PyInt count ? (int)count.Value : 0))),

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
        public string Substitute(string replacement, string subject, int count) =>
            _regex.Replace(subject, TranslateReplacement(replacement), count <= 0 ? -1 : count);

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
        public override string TypeName => "Match";

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

            "groupdict" => new PyBuiltinFunction("groupdict", ignored =>
            {
                var dict = new PyDict();

                // Only named groups appear; .NET reports numbered ones under their digits.
                _ = ignored;

                // Only named groups appear in a groupdict; .NET reports numbered ones
                // under their digits, so those are filtered out.
                foreach (var key in match.Groups.Keys.Where(static k => !IsNumeric(k)))
                {
                    dict.Set(new PyStr(key), match.Groups[key].Success
                        ? new PyStr(match.Groups[key].Value)
                        : PyNone.Instance);
                }

                return dict;
            }),

            "start" => new PyBuiltinFunction("start", arguments =>
                new PyInt(GroupOf(arguments).Index)),

            "end" => new PyBuiltinFunction("end", arguments =>
            {
                var group = GroupOf(arguments);
                return new PyInt(group.Index + group.Length);
            }),

            "span" => new PyBuiltinFunction("span", arguments =>
            {
                var group = GroupOf(arguments);
                return new PyTuple([new PyInt(group.Index), new PyInt(group.Index + group.Length)]);
            }),

            _ => null,
        };

        private static bool IsNumeric(string key) =>
            int.TryParse(key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _);

        private Group GroupOf(PyObject[] arguments) =>
            arguments.Length == 0 ? match : Resolve(arguments[0]);

        private PyObject Group(PyObject key)
        {
            var group = Resolve(key);
            return group.Success ? new PyStr(group.Value) : PyNone.Instance;
        }

        private Group Resolve(PyObject key)
        {
            var group = key is PyInt index ? match.Groups[(int)index.Value] : match.Groups[key.Display()];

            return group is null || (!group.Success && !match.Groups.Keys.Contains(group.Name))
                ? throw new PyRaise(PyErrors.IndexError("no such group"))
                : group;
        }
    }
}
