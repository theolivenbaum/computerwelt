using System.Globalization;
using System.Numerics;
using System.Text;

namespace Monty.Runtime;

/// <summary>Methods of the built-in types.</summary>
public static class BuiltinMethods
{
    /// <summary>Binds a method by name, or returns null when the type has no such method.</summary>
    public static PyObject? TryBind(VirtualMachine machine, PyObject target, string name) => target switch
    {
        PyStr => BindString(target, name),
        PyList => BindList(machine, target, name),
        PyDict => BindDict(machine, target, name),
        Modules.PyDefaultDict defaults => BindDict(machine, defaults.Entries, name),
        PySet => BindSet(target, name),
        PyTuple => BindTuple(target, name),
        PyBytes => BindBytes(target, name),
        PyInt => BindInt(target, name),
        PyFloat => BindFloat(target, name),
        _ => null,
    };

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
                // Sequence methods word this differently from mapping methods, and the
                // fixtures compare the message.
                throw new PyRaise(PyErrors.TypeError(self is PyList or PyTuple or PyStr or PySet
                    ? $"{self.TypeName}.{name}() takes exactly {(minimum == 1 ? "one argument" : minimum + " arguments")} ({arguments.Length} given)"
                    : arguments.Length < minimum
                        ? $"{name} expected at least {minimum} argument{(minimum == 1 ? string.Empty : "s")}, got {arguments.Length}"
                        : $"{name} expected at most {maximum} argument{(maximum == 1 ? string.Empty : "s")}, got {arguments.Length}"));
            }

            return implementation(self, arguments, keywords);
        });

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
                return Method(name, receiver, static (self, arguments, _) =>
                {
                    var keepEnds = arguments.Length > 0 && arguments[0].IsTruthy();
                    var text = ((PyStr)self).Value;

                    if (text.Length == 0)
                    {
                        return new PyList();
                    }

                    var lines = new List<PyObject>();
                    var start = 0;

                    for (var i = 0; i < text.Length; i++)
                    {
                        if (text[i] != '\n')
                        {
                            continue;
                        }

                        lines.Add(new PyStr(keepEnds ? text[start..(i + 1)] : text[start..i].TrimEnd('\r')));
                        start = i + 1;
                    }

                    if (start < text.Length)
                    {
                        lines.Add(new PyStr(text[start..]));
                    }

                    return new PyList(lines);
                });

            case "join":
                return Method(name, receiver, 1, static (self, arguments, _) =>
                {
                    var separator = ((PyStr)self).Value;
                    var builder = new StringBuilder();
                    var first = true;

                    foreach (var item in VirtualMachine.RequireIterable(arguments[0]))
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
                return Method(name, receiver, 2, (self, arguments, _) =>
                {
                    var text = ((PyStr)self).Value;
                    var from = Text(arguments[0], name, 1);
                    var to = Text(arguments[1], name, 2);
                    var count = arguments.Length > 2 ? Int(arguments[2], "count") : -1;

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
                    var prefixes = arguments[0] is PyTuple tuple
                        ? tuple.Items.Select(item => Text(item, name, 1))
                        : [Text(arguments[0], name, 1)];

                    // The optional start and end bounds narrow the region tested.
                    var start = arguments.Length > 1 && arguments[1] is not PyNone ? Int(arguments[1], "start") : 0;
                    var end = arguments.Length > 2 && arguments[2] is not PyNone ? Int(arguments[2], "end") : text.Length;

                    start = Math.Clamp(start < 0 ? text.Length + start : start, 0, text.Length);
                    end = Math.Clamp(end < 0 ? text.Length + end : end, start, text.Length);
                    var region = text[start..end];

                    return PyBool.Of(prefixes.Any(prefix => name == "startswith"
                        ? region.StartsWith(prefix, StringComparison.Ordinal)
                        : region.EndsWith(prefix, StringComparison.Ordinal)));
                });

            case "find" or "rfind" or "index" or "rindex":
                return Method(name, receiver, 1, (self, arguments, _) =>
                {
                    var text = ((PyStr)self).Value;
                    var needle = Text(arguments[0], name, 1);
                    var start = arguments.Length > 1 && arguments[1] is not PyNone ? Int(arguments[1], "start") : 0;
                    var end = arguments.Length > 2 && arguments[2] is not PyNone ? Int(arguments[2], "end") : text.Length;

                    start = Math.Clamp(start < 0 ? text.Length + start : start, 0, text.Length);
                    end = Math.Clamp(end < 0 ? text.Length + end : end, start, text.Length);

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
                return Method(name, receiver, 1, (self, arguments, _) =>
                {
                    var text = ((PyStr)self).Value;
                    var needle = Text(arguments[0], name, 1);

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

            case "isdigit":
                return Predicate(receiver, name, static text => text.Length > 0 && text.All(char.IsDigit));

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

            case "isnumeric" or "isdecimal":
                return Predicate(receiver, name, static text => text.Length > 0 && text.All(char.IsDigit));

            case "isidentifier":
                return Predicate(receiver, name, static text =>
                    text.Length > 0
                    && (char.IsLetter(text[0]) || text[0] == '_')
                    && text.All(static c => char.IsLetterOrDigit(c) || c == '_'));

            case "istitle":
                return Predicate(receiver, name, static text => text.Length > 0 && text == TitleCase(text));

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
                return Method(name, receiver, static (self, _, _) =>
                    new PyBytes(Encoding.UTF8.GetBytes(((PyStr)self).Value)));

            case "casefold":
                return Method(name, receiver, static (self, _, _) =>
                    new PyStr(((PyStr)self).Value.ToLowerInvariant()));

            case "rjust_placeholder":
                return null;

            case "expandtabs":
                return Method(name, receiver, (self, arguments, _) =>
                {
                    var size = arguments.Length > 0 ? Int(arguments[0], "tabsize") : 8;
                    var builder = new StringBuilder();
                    var column = 0;

                    foreach (var c in ((PyStr)self).Value)
                    {
                        if (c == '\t')
                        {
                            var advance = size - (column % size);
                            builder.Append(new string(' ', advance));
                            column += advance;
                            continue;
                        }

                        builder.Append(c);
                        column = c == '\n' ? 0 : column + 1;
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
        var separator = arguments.Length > 0 && arguments[0] is not PyNone ? arguments[0].Display() : null;
        var limit = arguments.Length > 1 ? Int(arguments[1], "maxsplit") : -1;

        if (keywords?.TryGetValue(new PyStr("maxsplit"), out var maxSplit) == true)
        {
            limit = Int(maxSplit, "maxsplit");
        }

        if (separator is null)
        {
            // Splitting on whitespace collapses runs and ignores leading and trailing ones.
            var pieces = limit < 0
                ? text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList()
                : SplitWhitespaceLimited(text, limit, fromRight);

            return new PyList([.. pieces.Select(static p => (PyObject)new PyStr(p))]);
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

    private static List<string> SplitWhitespaceLimited(string text, int limit, bool fromRight)
    {
        var all = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

        if (all.Count <= limit + 1)
        {
            return all;
        }

        return fromRight
            ? [string.Join(' ', all[..^limit]), .. all[^limit..]]
            : [.. all[..limit], string.Join(' ', all[limit..])];
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
                    ((PyList)self).Items.AddRange(VirtualMachine.RequireIterable(arguments[0]));
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
                    var index = items.FindIndex(item => item.PyEquals(arguments[0]));

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
                return Method(name, receiver, 1, static (self, arguments, _) =>
                {
                    var items = ((PyList)self).Items;
                    var index = items.FindIndex(item => item.PyEquals(arguments[0]));

                    return index >= 0
                        ? new PyInt(index)
                        : throw new PyRaise(PyErrors.ValueError($"{arguments[0].Repr()} is not in list"));
                });

            case "count":
                return Method(name, receiver, 1, static (self, arguments, _) =>
                    new PyInt(((PyList)self).Items.Count(item => item.PyEquals(arguments[0]))));

            case "reverse":
                return Method(name, receiver, static (self, _, _) =>
                {
                    ((PyList)self).Items.Reverse();
                    return PyNone.Instance;
                });

            case "sort":
                return Method(name, receiver, (self, _, keywords) =>
                {
                    var items = ((PyList)self).Items;
                    var sorted = Sorting.Sort(machine, items, keywords);
                    items.Clear();
                    items.AddRange(sorted);
                    return PyNone.Instance;
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

            // The three views are live-looking but materialised here; what matters is that
            // they are not lists, so their type names and set behaviour are right.
            case "keys":
                return Method(name, receiver, 0, 0, static (self, _, _) =>
                    new PyView("dict_keys", ((PyDict)self).Entries.Select(static e => e.Key), isSetLike: true));

            case "values":
                return Method(name, receiver, 0, 0, static (self, _, _) =>
                    new PyView("dict_values", ((PyDict)self).Entries.Select(static e => e.Value), isSetLike: false));

            case "items":
                return Method(name, receiver, 0, 0, static (self, _, _) =>
                    new PyView(
                        "dict_items",
                        ((PyDict)self).Entries.Select(static e => (PyObject)new PyTuple([e.Key, e.Value])),
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
                return Method(name, receiver, static (self, arguments, keywords) =>
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
                return Method(name, receiver, 1, 2, static (_, arguments, _) =>
                {
                    var dict = new PyDict();
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

    // ---- tuple, bytes, int, float ----

    private static PyObject? BindTuple(PyObject receiver, string name) => name switch
    {
        "count" => Method(name, receiver, 1, static (self, arguments, _) =>
            new PyInt(((PyTuple)self).Items.Count(item => item.PyEquals(arguments[0])))),

        "index" => Method(name, receiver, 1, static (self, arguments, _) =>
        {
            var items = ((PyTuple)self).Items;

            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].PyEquals(arguments[0]))
                {
                    return new PyInt(i);
                }
            }

            throw new PyRaise(PyErrors.ValueError($"tuple.index(x): x not in tuple"));
        }),

        _ => null,
    };

    private static PyObject? BindBytes(PyObject receiver, string name) => name switch
    {
        "decode" => Method(name, receiver, static (self, _, _) =>
            new PyStr(Encoding.UTF8.GetString(((PyBytes)self).Value))),

        "hex" => Method(name, receiver, static (self, _, _) =>
            new PyStr(Convert.ToHexStringLower(((PyBytes)self).Value))),

        _ => null,
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
