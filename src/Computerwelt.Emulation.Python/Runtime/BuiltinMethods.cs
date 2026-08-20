using System.Globalization;
using GlobalizationCategory = System.Globalization.UnicodeCategory;
using System.Numerics;
using System.Text;

namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>Methods of the built-in types.</summary>
public static class BuiltinMethods
{
    /// <summary>Binds a method by name, or returns null when the type has no such method.</summary>
    public static PyObject? TryBind(VirtualMachine machine, PyObject target, string name) => target switch
    {
        PyStr => BindString(target, name),
        PyList => BindList(machine, target, name),
        PyDict => BindDict(machine, target, name),
        PySet => BindSet(target, name),
        PyView view => BindView(view, name),
        PyTuple => BindTuple(target, name),
        PyBytes => BindBytes(target, name),
        PyInt => BindInt(target, name),
        PyFloat => BindFloat(target, name),
        _ => null,
    };

    /// <summary>
    /// The methods of a dict view: only <c>isdisjoint</c>, and only on the set-like ones.
    /// </summary>
    /// <remarks>
    /// A values view is deliberately left without it — it is not set-like, and a script that
    /// reaches for the method should hear that rather than get an answer.
    /// </remarks>
    private static PyObject? BindView(PyView receiver, string name) =>
        name == "isdisjoint" && receiver.IsSetLike
            ? Method(name, receiver, 1, 1, static (self, arguments, _) =>
                PyBool.Of(!VirtualMachine.RequireIterable(arguments[0]).Any(((PyView)self).Contains)))
            : null;

    private static PyBoundMethod Method(string name, PyObject receiver, Func<PyObject, PyObject[], PyDict?, PyObject> implementation) =>
        new(name, receiver, implementation);

    /// <summary>
    /// Wraps a method that needs at least <paramref name="minimum"/> arguments, so a
    /// missing one becomes a <c>TypeError</c> rather than a host index error.
    /// </summary>
    private static PyBoundMethod Method(
        string name,
        PyObject receiver,
        int minimum,
        Func<PyObject, PyObject[], PyDict?, PyObject> implementation) =>
        Method(name, receiver, minimum, int.MaxValue, implementation);

    /// <summary>
    /// Wraps a method with an argument-count range, so a missing or surplus argument
    /// becomes a <c>TypeError</c> rather than a host index error or a silent success.
    /// </summary>
    private static PyBoundMethod Method(
        string name,
        PyObject receiver,
        int minimum,
        int maximum,
        Func<PyObject, PyObject[], PyDict?, PyObject> implementation) =>
        new(name, receiver, (self, arguments, keywords) =>
        {
            if (arguments.Length < minimum || arguments.Length > maximum)
            {
                // Three wordings, and which one a method uses is not a matter of taste: a
                // fixed-arity method of a sequence type names its type and says "exactly";
                // one with a range of arities uses the argument clinic's "at least"; every
                // other method uses the older "expected" form. Scripts match on all three.
                throw new PyRaise(PyErrors.TypeError(
                    self is PyList or PyTuple or PyStr or PySet && minimum == maximum
                        ? $"{self.TypeName}.{name}() takes exactly {(minimum == 1 ? "one argument" : minimum + " arguments")} ({arguments.Length} given)"
                        : self is PyStr
                            ? arguments.Length < minimum
                                ? $"{name}() takes at least {minimum} positional argument{(minimum == 1 ? string.Empty : "s")} ({arguments.Length} given)"
                                : $"{name}() takes at most {maximum} argument{(maximum == 1 ? string.Empty : "s")} ({arguments.Length} given)"
                            : arguments.Length < minimum
                                ? $"{name} expected at least {minimum} argument{(minimum == 1 ? string.Empty : "s")}, got {arguments.Length}"
                                : $"{name} expected at most {maximum} argument{(maximum == 1 ? string.Empty : "s")}, got {arguments.Length}"));
            }

            return implementation(self, arguments, keywords);
        });

    /// <summary>
    /// Rejects more arguments than a method takes, counting positional and keyword together.
    /// </summary>
    /// <remarks>
    /// A parameter that may be spelled either way is one parameter however it is spelled, so
    /// giving it twice is a count error rather than a duplicate one.
    /// </remarks>
    private static void Total(string method, PyObject[] arguments, PyDict? keywords, int maximum)
    {
        var total = arguments.Length + (keywords?.Count ?? 0);

        if (total > maximum)
        {
            // With nothing positional the count being complained about is of keywords.
            var kind = arguments.Length == 0 ? "keyword argument" : "argument";

            throw new PyRaise(PyErrors.TypeError(
                $"{method}() takes at most {maximum} {kind}{(maximum == 1 ? string.Empty : "s")} ({total} given)"));
        }
    }

    /// <summary>
    /// Reads one named keyword, rejecting any other. A method that takes keywords must
    /// still refuse the ones it does not know.
    /// </summary>
    private static PyObject? Keyword(PyDict? keywords, string method, string name)
    {
        PyObject? found = null;

        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            if (key is PyStr text && string.Equals(text.Value, name, StringComparison.Ordinal))
            {
                found = value;
                continue;
            }

            throw new PyRaise(PyErrors.TypeError(
                $"{method}() got an unexpected keyword argument '{key.Display()}'"));
        }

        return found;
    }

    private static string Text(PyObject value, string method, int position) =>
        value is PyStr text
            ? text.Value
            : throw new PyRaise(PyErrors.TypeError(
                $"{method}() argument {position} must be str, not {value.TypeName}"));

    private static int Int(PyObject value, string what) =>
        value is PyInt integer
            ? integer.ToIndex()
            : throw new PyRaise(PyErrors.TypeError($"{what} must be an integer, not {value.TypeName}"));

    // ---- str ----

    private static PyObject? BindString(PyObject receiver, string name)
    {
        switch (name)
        {
            case "upper":
                return Method(name, receiver, static (self, _, _) => new PyStr(((PyStr)self).Value.ToUpperInvariant()));

            case "lower":
                return Method(name, receiver, static (self, _, _) => new PyStr(((PyStr)self).Value.ToLowerInvariant()));

            case "title":
                return Method(name, receiver, static (self, _, _) => new PyStr(TitleCase(((PyStr)self).Value)));

            case "capitalize":
                return Method(name, receiver, static (self, _, _) =>
                {
                    var text = ((PyStr)self).Value;
                    return new PyStr(text.Length == 0
                        ? text
                        : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant());
                });

            case "swapcase":
                return Method(name, receiver, static (self, _, _) => new PyStr(new string(
                    ((PyStr)self).Value.Select(static c =>
                        char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray())));

            case "strip" or "lstrip" or "rstrip":
                return Method(name, receiver, (self, arguments, _) =>
                {
                    var text = ((PyStr)self).Value;
                    var characters = arguments.Length > 0 && arguments[0] is not PyNone
                        ? Text(arguments[0], name, 1).ToCharArray()
                        : null;

                    return new PyStr(name switch
                    {
                        "lstrip" => characters is null ? text.TrimStart() : text.TrimStart(characters),
                        "rstrip" => characters is null ? text.TrimEnd() : text.TrimEnd(characters),
                        _ => characters is null ? text.Trim() : text.Trim(characters),
                    });
                });

            case "split" or "rsplit":
                return Method(name, receiver, (self, arguments, keywords) =>
                    Split(((PyStr)self).Value, arguments, keywords, fromRight: name == "rsplit"));

            case "splitlines":
                return Method(name, receiver, 0, 1, (self, arguments, keywords) =>
                {
                    Total(name, arguments, keywords, 1);

                    var keepEnds = arguments.Length > 0
                        ? arguments[0].IsTruthy()
                        : Keyword(keywords, name, "keepends")?.IsTruthy() ?? false;

                    var text = ((PyStr)self).Value;
                    var lines = new List<PyObject>();
                    var start = 0;

                    for (var i = 0; i < text.Length; i++)
                    {
                        if (text[i] is not ('\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e'))
                        {
                            continue;
                        }

                        // A CR immediately followed by an LF is one line ending, not two.
                        var end = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? i + 2 : i + 1;
                        lines.Add(new PyStr(keepEnds ? text[start..end] : text[start..i]));
                        start = end;
                        i = end - 1;
                    }

                    if (start < text.Length)
                    {
                        lines.Add(new PyStr(text[start..]));
                    }

                    return new PyList(lines);
                });

            case "join":
                return Method(name, receiver, 1, 1, static (self, arguments, _) =>
                {
                    var separator = ((PyStr)self).Value;
                    var builder = new StringBuilder();
                    var first = true;

                    // `join` words a non-iterable's refusal as a sentence of its own rather
                    // than naming the type, which is what CPython's str.join does.
                    IEnumerable<PyObject> pieces;

                    try
                    {
                        pieces = VirtualMachine.RequireIterable(arguments[0]);
                    }
                    catch (PyRaise raise)
                        when (raise.Exception.Message == $"'{arguments[0].TypeName}' object is not iterable")
                    {
                        throw new PyRaise(PyErrors.TypeError("can only join an iterable"));
                    }

                    foreach (var item in pieces)
                    {
                        if (item is not PyStr piece)
                        {
                            throw new PyRaise(PyErrors.TypeError(
                                $"sequence item 0: expected str instance, {item.TypeName} found"));
                        }

                        if (!first)
                        {
                            builder.Append(separator);
                        }

                        builder.Append(piece.Value);
                        first = false;
                    }

                    return new PyStr(builder.ToString());
                });

            case "replace":
                return Method(name, receiver, 2, 3, (self, arguments, keywords) =>
                {
                    var text = ((PyStr)self).Value;
                    var from = Text(arguments[0], name, 1);
                    var to = Text(arguments[1], name, 2);
                    var count = arguments.Length > 2 ? Int(arguments[2], "count")
                        : keywords?.TryGetValue(new PyStr("count"), out var limit) == true ? Int(limit, "count")
                        : -1;

                    if (count < 0)
                    {
                        return new PyStr(text.Replace(from, to, StringComparison.Ordinal));
                    }

                    var builder = new StringBuilder();
                    var position = 0;

                    while (count-- > 0)
                    {
                        var found = text.IndexOf(from, position, StringComparison.Ordinal);

                        if (found < 0 || from.Length == 0)
                        {
                            break;
                        }

                        builder.Append(text, position, found - position).Append(to);
                        position = found + from.Length;
                    }

                    builder.Append(text, position, text.Length - position);
                    return new PyStr(builder.ToString());
                });

            case "startswith" or "endswith":
                return Method(name, receiver, 1, (self, arguments, _) =>
                {
                    var text = ((PyStr)self).Value;

                    // The optional start and end bounds are validated before the affix is
                    // inspected, which is the order CPython reports errors in.
                    var (start, end) = Bounds(arguments, text.Length);
                    var region = text[start..end];

                    // A tuple of candidates is allowed, but every element must be a string;
                    // the error names the method rather than the argument position.
                    var prefixes = arguments[0] is PyTuple tuple
                        ? tuple.Items.Select(item => item is PyStr candidate
                            ? candidate.Value
                            : throw new PyRaise(PyErrors.TypeError(
                                $"tuple for {name} must only contain str, not {item.TypeName}")))
                        : [arguments[0] is PyStr only
                            ? only.Value
                            : throw new PyRaise(PyErrors.TypeError(
                                $"{name} first arg must be str or a tuple of str, not {arguments[0].TypeName}"))];

                    return PyBool.Of(prefixes.Any(prefix => name == "startswith"
                        ? region.StartsWith(prefix, StringComparison.Ordinal)
                        : region.EndsWith(prefix, StringComparison.Ordinal)));
                });

            case "find" or "rfind" or "index" or "rindex":
                return Method(name, receiver, 1, (self, arguments, _) =>
                {
                    var text = ((PyStr)self).Value;
                    var needle = Text(arguments[0], name, 1);
                    var (start, end) = Bounds(arguments, text.Length);
                    var region = text[start..end];
                    var found = name is "rfind" or "rindex"
                        ? region.LastIndexOf(needle, StringComparison.Ordinal)
                        : region.IndexOf(needle, StringComparison.Ordinal);

                    // `find` reports -1 for a miss; `index` raises. That is the only
                    // difference between them.
                    if (found < 0 && name is "index" or "rindex")
                    {
                        throw new PyRaise(PyErrors.ValueError("substring not found"));
                    }

                    return new PyInt(found < 0 ? -1 : found + start);
                });

            case "count":
                return Method(name, receiver, 1, 3, (self, arguments, _) =>
                {
                    var whole = ((PyStr)self).Value;
                    var needle = Text(arguments[0], name, 1);
                    var (from, to) = Bounds(arguments, whole.Length);
                    var text = whole[from..to];

                    if (needle.Length == 0)
                    {
                        return new PyInt(text.Length + 1);
                    }

                    var count = 0;
                    var position = 0;

                    while ((position = text.IndexOf(needle, position, StringComparison.Ordinal)) >= 0)
                    {
                        count++;
                        position += needle.Length;
                    }

                    return new PyInt(count);
                });

            case "format":
                return Method(name, receiver, static (self, arguments, keywords) =>
                    new PyStr(BraceFormatter.Format(((PyStr)self).Value, arguments, keywords)));

            // The three numeric predicates widen in turn: a decimal digit is a digit, and a
            // digit is numeric, but a vulgar fraction is only the last of the three.
            case "isdigit":
                return Predicate(receiver, name, static text => text.Length > 0 && text.All(IsDigitLike));

            case "isalpha":
                return Predicate(receiver, name, static text => text.Length > 0 && text.All(char.IsLetter));

            case "isalnum":
                return Predicate(receiver, name, static text => text.Length > 0 && text.All(char.IsLetterOrDigit));

            case "isspace":
                return Predicate(receiver, name, static text => text.Length > 0 && text.All(char.IsWhiteSpace));

            case "isupper":
                return Predicate(receiver, name, static text => text.Any(char.IsLetter) && !text.Any(char.IsLower));

            case "islower":
                return Predicate(receiver, name, static text => text.Any(char.IsLetter) && !text.Any(char.IsUpper));

            case "isdecimal":
                return Predicate(receiver, name, static text => text.Length > 0
                    && text.All(static c => char.GetUnicodeCategory(c) == GlobalizationCategory.DecimalDigitNumber));

            case "isnumeric":
                return Predicate(receiver, name, static text => text.Length > 0 && text.All(static c =>
                    char.GetUnicodeCategory(c) is GlobalizationCategory.DecimalDigitNumber
                        or GlobalizationCategory.LetterNumber
                        or GlobalizationCategory.OtherNumber));

            case "isascii":
                return Predicate(receiver, name, static text => text.All(char.IsAscii));

            case "isidentifier":
                return Predicate(receiver, name, Identifiers.IsIdentifier);

            case "istitle":
                return Predicate(receiver, name, static text => IsTitle(text));

            case "zfill":
                return Method(name, receiver, 1, (self, arguments, _) =>
                {
                    var text = ((PyStr)self).Value;
                    var width = Int(arguments[0], "width");

                    if (text.Length >= width)
                    {
                        return new PyStr(text);
                    }

                    // A sign stays in front of the padding.
                    return new PyStr(text.Length > 0 && text[0] is '-' or '+'
                        ? text[0] + text[1..].PadLeft(width - 1, '0')
                        : text.PadLeft(width, '0'));
                });

            case "ljust" or "rjust" or "center":
                return Method(name, receiver, 1, (self, arguments, _) =>
                {
                    var text = ((PyStr)self).Value;
                    var width = Int(arguments[0], "width");
                    var fill = arguments.Length > 1 ? Text(arguments[1], name, 2)[0] : ' ';

                    if (text.Length >= width)
                    {
                        return new PyStr(text);
                    }

                    if (name == "ljust")
                    {
                        return new PyStr(text.PadRight(width, fill));
                    }

                    if (name == "rjust")
                    {
                        return new PyStr(text.PadLeft(width, fill));
                    }

                    var left = (width - text.Length) / 2;
                    return new PyStr(new string(fill, left) + text + new string(fill, width - text.Length - left));
                });

            case "removeprefix" or "removesuffix":
                return Method(name, receiver, 1, (self, arguments, _) =>
                {
                    var text = ((PyStr)self).Value;
                    var affix = Text(arguments[0], name, 1);

                    if (name == "removeprefix")
                    {
                        return new PyStr(text.StartsWith(affix, StringComparison.Ordinal) ? text[affix.Length..] : text);
                    }

                    return new PyStr(affix.Length > 0 && text.EndsWith(affix, StringComparison.Ordinal)
                        ? text[..^affix.Length]
                        : text);
                });

            case "partition" or "rpartition":
                return Method(name, receiver, 1, (self, arguments, _) =>
                {
                    var text = ((PyStr)self).Value;
                    var separator = Text(arguments[0], name, 1);

                    if (separator.Length == 0)
                    {
                        throw new PyRaise(PyErrors.ValueError("empty separator"));
                    }

                    var found = name == "partition"
                        ? text.IndexOf(separator, StringComparison.Ordinal)
                        : text.LastIndexOf(separator, StringComparison.Ordinal);

                    if (found < 0)
                    {
                        // A miss still returns three parts, with the text in the end that
                        // matches the search direction.
                        return name == "partition"
                            ? new PyTuple([new PyStr(text), PyStr.Empty, PyStr.Empty])
                            : new PyTuple([PyStr.Empty, PyStr.Empty, new PyStr(text)]);
                    }

                    return new PyTuple([
                        new PyStr(text[..found]),
                        new PyStr(separator),
                        new PyStr(text[(found + separator.Length)..]),
                    ]);
                });

            case "encode":
                return Method(name, receiver, static (self, arguments, keywords) =>
                {
                    var given = Clinic("encode", ["encoding", "errors"], arguments, keywords);

                    return new PyBytes(Codecs.Encode(
                        ((PyStr)self).Value,
                        CodecArgument("encode", given, 0, "encoding", "utf-8"),
                        CodecArgument("encode", given, 1, "errors", "strict")));
                });

            case "casefold":
                return Method(name, receiver, static (self, _, _) =>
                    new PyStr(((PyStr)self).Value.ToLowerInvariant()));

            case "rjust_placeholder":
                return null;

            case "expandtabs":
                return Method(name, receiver, (self, arguments, keywords) =>
                {
                    Total(name, arguments, keywords, 1);

                    var size = arguments.Length > 0
                        ? Int(arguments[0], "tabsize")
                        : Keyword(keywords, name, "tabsize") is { } named ? Int(named, "tabsize") : 8;
                    var builder = new StringBuilder();
                    var column = 0;

                    foreach (var c in ((PyStr)self).Value)
                    {
                        if (c == '\t')
                        {
                            // A tab size of zero or less deletes the tab rather than
                            // advancing to a tab stop there is no room for.
                            var advance = size > 0 ? size - (column % size) : 0;
                            builder.Append(' ', advance);
                            column += advance;
                            continue;
                        }

                        builder.Append(c);

                        // Both line terminators start a new line, so both reset the column.
                        column = c is '\n' or '\r' ? 0 : column + 1;
                    }

                    return new PyStr(builder.ToString());
                });

            default:
                return null;
        }
    }

    private static PyBoundMethod Predicate(PyObject receiver, string name, Func<string, bool> test) =>
        Method(name, receiver, (self, _, _) => PyBool.Of(test(((PyStr)self).Value)));

    private static string TitleCase(string text)
    {
        var builder = new StringBuilder(text.Length);
        var startOfWord = true;

        foreach (var c in text)
        {
            builder.Append(startOfWord ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            startOfWord = !char.IsLetter(c);
        }

        return builder.ToString();
    }

    private static PyObject Split(string text, PyObject[] arguments, PyDict? keywords, bool fromRight)
    {
        var given = arguments.Length > 0 ? arguments[0]
            : keywords?.TryGetValue(new PyStr("sep"), out var named) == true ? named
            : null;

        var separator = given is not (null or PyNone) ? given.Display() : null;
        var limit = arguments.Length > 1 ? Int(arguments[1], "maxsplit") : -1;

        if (keywords?.TryGetValue(new PyStr("maxsplit"), out var maxSplit) == true)
        {
            limit = Int(maxSplit, "maxsplit");
        }

        if (separator is null)
        {
            // Splitting on whitespace collapses runs and ignores leading and trailing ones.
            return new PyList([.. SplitWhitespace(text, limit, fromRight).Select(static p => (PyObject)new PyStr(p))]);
        }

        if (separator.Length == 0)
        {
            throw new PyRaise(PyErrors.ValueError("empty separator"));
        }

        var parts = text.Split(separator);

        if (limit >= 0 && parts.Length > limit + 1)
        {
            parts = fromRight
                ? [string.Join(separator, parts[..^limit]), .. parts[^limit..]]
                : [.. parts[..limit], string.Join(separator, parts[limit..])];
        }

        return new PyList([.. parts.Select(static p => (PyObject)new PyStr(p))]);
    }

    /// <summary>
    /// Splits on runs of whitespace, honouring <c>maxsplit</c>.
    /// </summary>
    /// <remarks>
    /// The remainder past the split limit keeps its original whitespace — <c>rsplit</c> of
    /// <c>'  a  b  '</c> at one split is <c>['  a', 'b']</c> — so this scans positions
    /// rather than splitting and rejoining.
    /// </remarks>
    private static List<string> SplitWhitespace(string text, int limit, bool fromRight)
    {
        var pieces = new List<string>();

        if (fromRight)
        {
            var end = text.Length;

            while (true)
            {
                while (end > 0 && char.IsWhiteSpace(text[end - 1]))
                {
                    end--;
                }

                if (end == 0)
                {
                    break;
                }

                if (limit >= 0 && pieces.Count == limit)
                {
                    pieces.Insert(0, text[..end]);
                    break;
                }

                var last = end;

                while (end > 0 && !char.IsWhiteSpace(text[end - 1]))
                {
                    end--;
                }

                pieces.Insert(0, text[end..last]);
            }

            return pieces;
        }

        var start = 0;

        while (true)
        {
            while (start < text.Length && char.IsWhiteSpace(text[start]))
            {
                start++;
            }

            if (start == text.Length)
            {
                break;
            }

            if (limit >= 0 && pieces.Count == limit)
            {
                pieces.Add(text[start..]);
                break;
            }

            var first = start;

            while (start < text.Length && !char.IsWhiteSpace(text[start]))
            {
                start++;
            }

            pieces.Add(text[first..start]);
        }

        return pieces;
    }

    // ---- list ----

    private static PyObject? BindList(VirtualMachine machine, PyObject receiver, string name)
    {
        switch (name)
        {
            case "append":
                return Method(name, receiver, 1, 1, static (self, arguments, _) =>
                {
                    ((PyList)self).Items.Add(arguments[0]);
                    return PyNone.Instance;
                });

            case "extend":
                return Method(name, receiver, 1, static (self, arguments, _) =>
                {
                    // `x.extend(x)` appends what the list held, not what it grows to, so
                    // the source is read out before the target is touched.
                    var extra = VirtualMachine.RequireIterable(arguments[0]).ToList();
                    ((PyList)self).Items.AddRange(extra);
                    return PyNone.Instance;
                });

            case "insert":
                return Method(name, receiver, 2, 2, static (self, arguments, _) =>
                {
                    var items = ((PyList)self).Items;
                    var index = Int(arguments[0], "index");
                    // Insert clamps rather than raising, unlike indexing.
                    index = Math.Clamp(index < 0 ? items.Count + index : index, 0, items.Count);
                    items.Insert(index, arguments[1]);
                    return PyNone.Instance;
                });

            case "pop":
                return Method(name, receiver, static (self, arguments, _) =>
                {
                    var items = ((PyList)self).Items;

                    if (items.Count == 0)
                    {
                        throw new PyRaise(PyErrors.IndexError("pop from empty list"));
                    }

                    var index = arguments.Length > 0 ? Int(arguments[0], "index") : items.Count - 1;
                    index = PyStr.Normalize(index, items.Count, "pop index out of range");

                    var value = items[index];
                    items.RemoveAt(index);
                    return value;
                });

            case "remove":
                return Method(name, receiver, 1, static (self, arguments, _) =>
                {
                    var items = ((PyList)self).Items;
                    var index = items.FindIndex(item => PyObject.SameOrEqual(item, arguments[0]));

                    if (index < 0)
                    {
                        throw new PyRaise(PyErrors.ValueError("list.remove(x): x not in list"));
                    }

                    items.RemoveAt(index);
                    return PyNone.Instance;
                });

            case "clear":
                return Method(name, receiver, static (self, _, _) =>
                {
                    ((PyList)self).Items.Clear();
                    return PyNone.Instance;
                });

            case "copy":
                return Method(name, receiver, static (self, _, _) => new PyList([.. ((PyList)self).Items]));

            case "index":
                return Method(name, receiver, 1, 3, static (self, arguments, _) =>
                {
                    var items = ((PyList)self).Items;
                    var index = IndexOf(items, arguments);

                    return index >= 0
                        ? new PyInt(index)
                        : throw new PyRaise(PyErrors.ValueError("list.index(x): x not in list"));
                });

            case "count":
                return Method(name, receiver, 1, static (self, arguments, _) =>
                    new PyInt(((PyList)self).Items.Count(item => PyObject.SameOrEqual(item, arguments[0]))));

            case "reverse":
                return Method(name, receiver, static (self, _, _) =>
                {
                    ((PyList)self).Items.Reverse();
                    return PyNone.Instance;
                });

            case "sort":
                return Method(name, receiver, (self, _, keywords) =>
                {
                    // CPython detaches the buffer for the duration of the sort, so a `key`
                    // callback that looks at the list sees it empty; re-populating it is a
                    // ValueError, raised only after the sorted buffer is put back.
                    var items = ((PyList)self).Items;
                    var detached = new List<PyObject>(items);
                    items.Clear();

                    List<PyObject> sorted;

                    try
                    {
                        sorted = Sorting.Sort(machine, detached, keywords);
                    }
                    catch
                    {
                        items.Clear();
                        items.AddRange(detached);
                        throw;
                    }

                    var modified = items.Count != 0;
                    items.Clear();
                    items.AddRange(sorted);

                    return modified
                        ? throw new PyRaise(PyErrors.ValueError("list modified during sort"))
                        : PyNone.Instance;
                });

            default:
                return null;
        }
    }

    // ---- dict ----

    private static PyObject? BindDict(VirtualMachine machine, PyObject receiver, string name)
    {
        switch (name)
        {
            case "get":
                return Method(name, receiver, 1, 2, static (self, arguments, _) =>
                    ((PyDict)self).TryGetValue(arguments[0], out var value)
                        ? value
                        : arguments.Length > 1 ? arguments[1] : PyNone.Instance);

            // The three views read their dict on every access, so one taken before a
            // mutation still shows what the dict holds now.
            case "keys":
                return Method(name, receiver, 0, 0, static (self, _, _) =>
                    new PyView("dict_keys", (PyDict)self, static e => e.Key, isSetLike: true));

            case "values":
                return Method(name, receiver, 0, 0, static (self, _, _) =>
                    new PyView("dict_values", (PyDict)self, static e => e.Value, isSetLike: false));

            case "items":
                return Method(name, receiver, 0, 0, static (self, _, _) =>
                    new PyView(
                        "dict_items",
                        (PyDict)self,
                        static e => new PyTuple([e.Key, e.Value]),
                        isSetLike: true));

            case "pop":
                return Method(name, receiver, 1, 2, static (self, arguments, _) =>
                {
                    var dict = (PyDict)self;

                    if (dict.TryGetValue(arguments[0], out var value))
                    {
                        dict.Remove(arguments[0]);
                        return value;
                    }

                    return arguments.Length > 1
                        ? arguments[1]
                        : throw new PyRaise(PyErrors.KeyError(arguments[0]));
                });

            case "popitem":
                return Method(name, receiver, static (self, _, _) =>
                {
                    var dict = (PyDict)self;

                    if (dict.Count == 0)
                    {
                        throw new PyRaise(PyErrors.KeyError(new PyStr("popitem(): dictionary is empty")));
                    }

                    // LIFO, as Python 3.7 onwards specifies.
                    var last = dict.Entries[^1];
                    dict.Remove(last.Key);
                    return new PyTuple([last.Key, last.Value]);
                });

            case "setdefault":
                return Method(name, receiver, 1, static (self, arguments, _) =>
                {
                    var dict = (PyDict)self;

                    if (dict.TryGetValue(arguments[0], out var existing))
                    {
                        return existing;
                    }

                    var value = arguments.Length > 1 ? arguments[1] : PyNone.Instance;
                    dict.Set(arguments[0], value);
                    return value;
                });

            case "update":
                return Method(name, receiver, 0, 1, static (self, arguments, keywords) =>
                {
                    var dict = (PyDict)self;

                    if (arguments.Length > 0)
                    {
                        if (arguments[0] is PyDict source)
                        {
                            // `d.update(d)` is legal, so the entries are snapshotted
                            // before any of them are written back.
                            foreach (var (key, value) in source.Entries.ToList())
                            {
                                dict.Set(key, value);
                            }
                        }
                        else
                        {
                            foreach (var pair in VirtualMachine.RequireIterable(arguments[0]))
                            {
                                var items = VirtualMachine.RequireIterable(pair).ToList();

                                if (items.Count != 2)
                                {
                                    throw new PyRaise(PyErrors.ValueError(
                                        $"dictionary update sequence element #0 has length {items.Count}; 2 is required"));
                                }

                                dict.Set(items[0], items[1]);
                            }
                        }
                    }

                    if (keywords is not null)
                    {
                        foreach (var (key, value) in keywords.Entries)
                        {
                            dict.Set(key, value);
                        }
                    }

                    return PyNone.Instance;
                });

            case "clear":
                return Method(name, receiver, static (self, _, _) =>
                {
                    ((PyDict)self).Clear();
                    return PyNone.Instance;
                });

            case "copy":
                return Method(name, receiver, static (self, _, _) => ((PyDict)self).Copy());

            case "fromkeys":
                return Method(name, receiver, 1, 2, static (self, arguments, _) =>
                {
                    // A classmethod upstream, so it builds `cls()` — for a subclass that is
                    // an instance of the subclass, freshly constructed with no arguments.
                    var dict = ((PyDict)self).Fresh();
                    var value = arguments.Length > 1 ? arguments[1] : PyNone.Instance;

                    foreach (var key in VirtualMachine.RequireIterable(arguments[0]))
                    {
                        dict.Set(key, value);
                    }

                    return dict;
                });

            default:
                _ = machine;
                return null;
        }
    }

    // ---- set ----

    private static PyObject? BindSet(PyObject receiver, string name)
    {
        switch (name)
        {
            case "add":
                return Method(name, receiver, 1, static (self, arguments, _) =>
                {
                    ((PySet)self).Add(arguments[0]);
                    return PyNone.Instance;
                });

            case "discard":
                return Method(name, receiver, 1, static (self, arguments, _) =>
                {
                    ((PySet)self).Remove(arguments[0]);
                    return PyNone.Instance;
                });

            case "remove":
                return Method(name, receiver, 1, static (self, arguments, _) =>
                    ((PySet)self).Remove(arguments[0])
                        ? PyNone.Instance
                        : throw new PyRaise(PyErrors.KeyError(arguments[0])));

            case "clear":
                return Method(name, receiver, static (self, _, _) =>
                {
                    ((PySet)self).Clear();
                    return PyNone.Instance;
                });

            case "copy":
                return Method(name, receiver, static (self, _, _) => new PySet(((PySet)self).Items));

            case "union":
                return Method(name, receiver, static (self, arguments, _) =>
                {
                    var result = new PySet(((PySet)self).Items);

                    foreach (var argument in arguments)
                    {
                        foreach (var item in VirtualMachine.RequireIterable(argument))
                        {
                            result.Add(item);
                        }
                    }

                    return result;
                });

            case "intersection":
                return Method(name, receiver, static (self, arguments, _) =>
                {
                    var result = ((PySet)self).Items.ToList();

                    foreach (var argument in arguments)
                    {
                        var other = new PySet(VirtualMachine.RequireIterable(argument));
                        result = result.Where(other.Contains).ToList();
                    }

                    return new PySet(result);
                });

            case "difference":
                return Method(name, receiver, static (self, arguments, _) =>
                {
                    var result = ((PySet)self).Items.ToList();

                    foreach (var argument in arguments)
                    {
                        var other = new PySet(VirtualMachine.RequireIterable(argument));
                        result = result.Where(item => !other.Contains(item)).ToList();
                    }

                    return new PySet(result);
                });

            case "symmetric_difference":
                return Method(name, receiver, 1, static (self, arguments, _) =>
                {
                    var left = (PySet)self;
                    var right = new PySet(VirtualMachine.RequireIterable(arguments[0]));

                    return new PySet(left.Items.Where(item => !right.Contains(item))
                        .Concat(right.Items.Where(item => !left.Contains(item))));
                });

            case "issubset":
                return Method(name, receiver, 1, static (self, arguments, _) =>
                {
                    var other = new PySet(VirtualMachine.RequireIterable(arguments[0]));
                    return PyBool.Of(((PySet)self).Items.All(other.Contains));
                });

            case "issuperset":
                return Method(name, receiver, 1, static (self, arguments, _) =>
                {
                    var set = (PySet)self;
                    return PyBool.Of(VirtualMachine.RequireIterable(arguments[0]).All(set.Contains));
                });

            case "isdisjoint":
                return Method(name, receiver, 1, static (self, arguments, _) =>
                {
                    var set = (PySet)self;
                    return PyBool.Of(!VirtualMachine.RequireIterable(arguments[0]).Any(set.Contains));
                });

            case "update":
                return Method(name, receiver, static (self, arguments, _) =>
                {
                    var set = (PySet)self;

                    foreach (var argument in arguments)
                    {
                        foreach (var item in VirtualMachine.RequireIterable(argument))
                        {
                            set.Add(item);
                        }
                    }

                    return PyNone.Instance;
                });

            case "pop":
                return Method(name, receiver, static (self, _, _) =>
                {
                    var set = (PySet)self;

                    if (set.Count == 0)
                    {
                        throw new PyRaise(PyErrors.KeyError(new PyStr("pop from an empty set")));
                    }

                    var item = set.Items.First();
                    set.Remove(item);
                    return item;
                });

            default:
                return null;
        }
    }

    /// <summary>
    /// Finds a value in a sequence, honouring the optional <c>start</c> and <c>end</c>
    /// bounds, or -1 when it is not there.
    /// </summary>
    /// <remarks>
    /// The bounds are clamped rather than validated: <c>index(x, 5, 2)</c> over a
    /// three-element list searches an empty range and reports "not in list", where indexing
    /// the underlying storage with those numbers would fault.
    /// </remarks>
    private static int IndexOf(IReadOnlyList<PyObject> items, PyObject[] arguments)
    {
        var start = arguments.Length > 1 ? Bound(arguments[1], items.Count) : 0;
        var end = arguments.Length > 2 ? Bound(arguments[2], items.Count) : items.Count;

        for (var i = start; i < end; i++)
        {
            if (PyObject.SameOrEqual(items[i], arguments[0]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Resolves a sequence bound, counting a negative one from the end.</summary>
    /// <remarks>
    /// The arithmetic is done in <see cref="BigInteger"/> because these bounds clip rather
    /// than overflow: `lst.index(x, -(2**63))` is a plain search from the start, not an error.
    /// </remarks>
    private static int Bound(PyObject value, int length)
    {
        var index = value is PyInt integer ? integer.Value : BigInteger.Zero;

        if (index < 0)
        {
            index += length;
        }

        return (int)BigInteger.Clamp(index, 0, length);
    }

    // ---- tuple, bytes, int, float ----

    /// <summary>
    /// Whether a character is a digit: a decimal one, or one of the superscript, subscript
    /// and enclosed forms that stand for a single digit.
    /// </summary>
    private static bool IsDigitLike(char c)
    {
        var category = char.GetUnicodeCategory(c);

        if (category == GlobalizationCategory.DecimalDigitNumber)
        {
            return true;
        }

        // A fraction is numeric but not a digit, which is what the whole-number test rules
        // out — as does a value that stands for more than one digit.
        var value = CharUnicodeInfo.GetNumericValue(c);

        return category == GlobalizationCategory.OtherNumber
            && value is >= 0 and <= 9
            && value == Math.Floor(value);
    }

    /// <summary>
    /// Whether a string is title-cased: every cased run starts upper and continues lower.
    /// </summary>
    /// <remarks>
    /// Not the same as comparing against `title()`: an apostrophe is uncased, so `They'Re`
    /// is title-cased even though it does not read like it.
    /// </remarks>
    private static bool IsTitle(string text)
    {
        var cased = false;
        var previousCased = false;

        foreach (var c in text)
        {
            if (char.IsUpper(c) || char.GetUnicodeCategory(c) == GlobalizationCategory.TitlecaseLetter)
            {
                if (previousCased)
                {
                    return false;
                }

                previousCased = true;
                cased = true;
            }
            else if (char.IsLower(c))
            {
                if (!previousCased)
                {
                    return false;
                }

                cased = true;
            }
            else
            {
                previousCased = false;
            }
        }

        return cased;
    }

    private static PyObject? BindTuple(PyObject receiver, string name) => name switch
    {
        "count" => Method(name, receiver, 1, static (self, arguments, _) =>
            new PyInt(((PyTuple)self).Items.Count(item => PyObject.SameOrEqual(item, arguments[0])))),

        "index" => Method(name, receiver, 1, 3, static (self, arguments, _) =>
        {
            var index = IndexOf(((PyTuple)self).Items, arguments);

            return index >= 0
                ? new PyInt(index)
                : throw new PyRaise(PyErrors.ValueError("tuple.index(x): x not in tuple"));
        }),

        _ => null,
    };

    private static PyObject? BindBytes(PyObject receiver, string name)
    {
        switch (name)
        {
            case "decode":
                return Method(name, receiver, static (self, arguments, keywords) =>
                {
                    var given = Clinic("decode", ["encoding", "errors"], arguments, keywords);

                    return new PyStr(Codecs.Decode(
                        ((PyBytes)self).Value,
                        CodecArgument("decode", given, 0, "encoding", "utf-8"),
                        CodecArgument("decode", given, 1, "errors", "strict")));
                });

            case "fromhex":
                // A classmethod, so it also answers on an instance and ignores its value.
                return Method(name, receiver, 1, 1, static (_, arguments, _) =>
                    TypeRegistry.FromHex(arguments[0].Display()));

            case "hex":
                return Method(name, receiver, 0, 2, static (self, arguments, _) =>
                    new PyStr(Hex(((PyBytes)self).Value, arguments)));
        }

        // Every remaining bytes method is its str namesake applied to the bytes one at a
        // time. Latin-1 maps 0-255 onto the first 256 code points and back without loss,
        // so viewing the bytes as text lets one implementation serve both types.
        if (BindString(Latin1(receiver), name) is null)
        {
            return null;
        }

        return Method(name, receiver, (self, arguments, keywords) =>
        {
            // Text arguments are the one thing the shared implementation must not accept:
            // `b'x'.startswith('x')` is a type error, not a match.
            if (arguments.Length > 0 && Text(arguments[0]) is { } wrong)
            {
                throw new PyRaise(PyErrors.TypeError(name is "startswith" or "endswith"
                    ? $"{name} first arg must be bytes or a tuple of bytes, not {wrong}"
                    : $"argument should be integer or bytes-like object, not '{wrong}'"));
            }

            return FromLatin1(((PyBoundMethod)BindString(Latin1(self), name)!).Invoke(
                [.. arguments.Select(Latin1)], keywords));
        });
    }

    /// <summary>
    /// Resolves the optional <c>start</c> and <c>end</c> arguments the search methods take,
    /// clamping them to the string the way Python does rather than raising.
    /// </summary>
    private static (int Start, int End) Bounds(PyObject[] arguments, int length)
    {
        var start = arguments.Length > 1 && arguments[1] is not PyNone ? Bound(arguments[1], "start") : 0;
        var end = arguments.Length > 2 && arguments[2] is not PyNone ? Bound(arguments[2], "end") : length;

        return (start, Math.Max(start, end));

        // A bound far outside the string clamps to its nearest end rather than raising, so
        // a bound of -2**63 must not be narrowed to an int first.
        int Bound(PyObject value, string what)
        {
            if (value is not PyInt integer)
            {
                _ = what;
                throw new PyRaise(PyErrors.TypeError(
                    "slice indices must be integers or None or have an __index__ method"));
            }

            var index = integer.Value;

            return index >= length ? length
                : index <= -length ? 0
                : (int)(index < 0 ? length + index : index);
        }
    }

    /// <summary>
    /// Reads <c>encode</c>/<c>decode</c>'s encoding or errors argument, given positionally
    /// or by name. A wrong type is reported the way CPython's argument clinic does, which
    /// spells a lone <c>None</c> as "None" rather than "NoneType".
    /// </summary>
    private static string CodecArgument(string method, PyObject?[] given, int position, string name, string fallback) =>
        given[position] switch
        {
            null => fallback,
            PyStr text => text.Value,
            PyNone => throw new PyRaise(PyErrors.TypeError(
                $"{method}() argument '{name}' must be str, not None")),
            var bad => throw new PyRaise(PyErrors.TypeError(
                $"{method}() argument '{name}' must be str, not {bad.TypeName}")),
        };

    /// <summary>
    /// Binds a method's arguments the way the argument clinic does.
    /// </summary>
    /// <remarks>
    /// Binding happens in full before any value is converted, which is why an unknown
    /// keyword or a duplicated parameter is reported even when a positional also has the
    /// wrong type.
    /// </remarks>
    private static PyObject?[] Clinic(string method, string[] names, PyObject[] arguments, PyDict? keywords)
    {
        Total(method, arguments, keywords, names.Length);

        var given = new PyObject?[names.Length];

        for (var i = 0; i < arguments.Length; i++)
        {
            given[i] = arguments[i];
        }

        string? unknown = null;
        int? conflict = null;

        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            var position = Array.IndexOf(names, key.Display());

            if (position < 0)
            {
                unknown ??= key.Display();
            }
            else if (given[position] is not null)
            {
                conflict ??= position;
            }
            else
            {
                given[position] = value;
            }
        }

        if (conflict is { } clash)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"argument for {method}() given by name ('{names[clash]}') and position ({clash + 1})"));
        }

        return unknown is null
            ? given
            : throw new PyRaise(PyErrors.TypeError(
                $"{method}() got an unexpected keyword argument '{unknown}'"));
    }

    /// <summary>
    /// Renders bytes as hex, optionally separated into groups.
    /// </summary>
    /// <remarks>
    /// A positive group size counts from the right, so an odd tail lands at the front;
    /// a negative one counts from the left. CPython parses the size as a C int, so a value
    /// outside that range overflows rather than saturating.
    /// </remarks>
    private static string Hex(byte[] value, PyObject[] arguments)
    {
        var text = Convert.ToHexStringLower(value);

        if (arguments.Length == 0)
        {
            return text;
        }

        var separator = Text(arguments[0], "hex", 1);

        if (!separator.All(char.IsAscii))
        {
            throw new PyRaise(PyErrors.ValueError("sep must be ASCII."));
        }

        var group = 1;

        if (arguments.Length > 1)
        {
            if (arguments[1] is not PyInt size)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"hex() argument 2 must be int, not {arguments[1].TypeName}"));
            }

            if (size.Value > int.MaxValue || size.Value < int.MinValue)
            {
                throw new PyRaise(new PyException(
                    PyExceptionType.OverflowError, "Python int too large to convert to C int"));
            }

            group = (int)size.Value;
        }

        if (group == 0 || value.Length == 0)
        {
            return text;
        }

        var pieces = new List<string>();

        // A group wider than the value is one group, so the width is capped before it can
        // overflow an int.
        var width = (int)Math.Min(Math.Abs((long)group) * 2, text.Length);

        if (group > 0)
        {
            for (var end = text.Length; end > 0;)
            {
                var start = Math.Max(0, end - width);
                pieces.Insert(0, text[start..end]);
                end = start;
            }
        }
        else
        {
            for (var start = 0; start < text.Length; start += width)
            {
                pieces.Add(text[start..Math.Min(start + width, text.Length)]);
            }
        }

        return string.Join(separator, pieces);
    }

    /// <summary>The type name of a text argument a bytes method must reject, else null.</summary>
    private static string? Text(PyObject value) => value switch
    {
        PyStr => "str",
        PyTuple items when items.Items.Any(static item => item is PyStr) => "str",
        _ => null,
    };

    /// <summary>
    /// Views bytes as text, one code point per byte, recursing into a sequence argument so
    /// that <c>b','.join([b'a'])</c> reaches the str implementation as strings. Anything
    /// else passes through.
    /// </summary>
    private static PyObject Latin1(PyObject value) => value switch
    {
        PyBytes bytes => new PyStr(Encoding.Latin1.GetString(bytes.Value)),
        PyList items => new PyList([.. items.Items.Select(Latin1)]),
        PyTuple items => new PyTuple([.. items.Items.Select(Latin1)]),
        PyStr or PyInt or PyNone => value,

        // `join` takes any iterable, including a hand-written one, and its items are the
        // bytes to convert.
        _ => value.Iterate() is { } sequence ? new PyList([.. sequence.Select(Latin1)]) : value,
    };

    /// <summary>Converts a str result — or a container of them — back to bytes.</summary>
    private static PyObject FromLatin1(PyObject value) => value switch
    {
        PyStr text => new PyBytes(Encoding.Latin1.GetBytes(text.Value)),
        PyList items => new PyList([.. items.Items.Select(FromLatin1)]),
        PyTuple items => new PyTuple([.. items.Items.Select(FromLatin1)]),
        _ => value,
    };

    private static PyObject? BindInt(PyObject receiver, string name) => name switch
    {
        "bit_length" => Method(name, receiver, static (self, _, _) =>
        {
            var value = BigInteger.Abs(((PyInt)self).Value);
            var bits = 0;

            while (!value.IsZero)
            {
                value >>= 1;
                bits++;
            }

            return new PyInt(bits);
        }),

        "to_bytes" => Method(name, receiver, static (self, arguments, _) =>
        {
            var length = arguments.Length > 0 ? Int(arguments[0], "length") : 1;
            var bigEndian = arguments.Length > 1 && arguments[1].Display() == "big";
            var bytes = ((PyInt)self).Value.ToByteArray(isUnsigned: true, isBigEndian: bigEndian);
            var result = new byte[length];

            if (bigEndian)
            {
                bytes.AsSpan(Math.Max(0, bytes.Length - length)).CopyTo(result.AsSpan(Math.Max(0, length - bytes.Length)));
            }
            else
            {
                bytes.AsSpan(0, Math.Min(length, bytes.Length)).CopyTo(result);
            }

            return new PyBytes(result);
        }),

        _ => null,
    };

    private static PyObject? BindFloat(PyObject receiver, string name) => name switch
    {
        "is_integer" => Method(name, receiver, static (self, _, _) =>
            PyBool.Of(double.IsInteger(((PyFloat)self).Value))),

        _ => null,
    };
}
