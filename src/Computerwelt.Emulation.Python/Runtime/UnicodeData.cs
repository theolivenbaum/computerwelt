using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// The Unicode character database, as far as the port needs it.
/// </summary>
/// <remarks>
/// <para>
/// The tables are embedded rather than taken from the host runtime, for two reasons: the
/// host has no character-name API at all — which <c>\N{...}</c>, <c>unicodedata.name</c> and
/// the <c>namereplace</c> error handler all need — and a sandbox whose answers changed with
/// the host's ICU version would not be reproducible. Everything here is one Unicode release,
/// named by <see cref="Version"/>, so the answers agree with each other.
/// </para>
/// <para>
/// The blob holds the general categories as ranges, the non-zero combining classes, the
/// explicitly named code points, and the ranges whose names are algorithmic. Hangul
/// syllables are not listed at all: their names are composed from the jamo tables, which is
/// cheaper than the eleven thousand entries would be.
/// </para>
/// </remarks>
public static class UnicodeData
{
    private const int HangulBase = 0xAC00;
    private const int HangulCount = 11172;

    private static readonly string[] InitialJamo =
    [
        "G", "GG", "N", "D", "DD", "R", "M", "B", "BB", "S", "SS", "", "J", "JJ",
        "C", "K", "T", "P", "H",
    ];

    private static readonly string[] MedialJamo =
    [
        "A", "AE", "YA", "YAE", "EO", "E", "YEO", "YE", "O", "WA", "WAE", "OE", "YO",
        "U", "WEO", "WE", "WI", "YU", "EU", "YI", "I",
    ];

    private static readonly string[] FinalJamo =
    [
        "", "G", "GG", "GS", "N", "NJ", "NH", "D", "L", "LG", "LM", "LB", "LS", "LT",
        "LP", "LH", "M", "B", "BS", "S", "SS", "NG", "J", "C", "K", "T", "P", "H",
    ];

    private const int JamoLeadBase = 0x1100;
    private const int JamoVowelBase = 0x1161;
    private const int JamoTailBase = 0x11A7;
    private const int JamoVowelCount = 21;
    private const int JamoTailCount = 28;

    private static readonly Lazy<Tables> Loaded = new(Load, isThreadSafe: true);

    /// <summary>The Unicode release these tables come from.</summary>
    public static string Version => Loaded.Value.Version;

    /// <summary>The general category of a code point, such as <c>Lu</c>.</summary>
    /// <param name="codePoint">The code point.</param>
    /// <returns>The two-letter category.</returns>
    public static string Category(int codePoint)
    {
        var ranges = Loaded.Value.Categories;
        var low = 0;
        var high = ranges.Length - 1;

        while (low <= high)
        {
            var middle = (low + high) / 2;

            if (codePoint < ranges[middle].Start)
            {
                high = middle - 1;
            }
            else if (codePoint > ranges[middle].End)
            {
                low = middle + 1;
            }
            else
            {
                return ranges[middle].Category;
            }
        }

        // Everything the table does not cover is unassigned.
        return "Cn";
    }

    /// <summary>The canonical combining class of a code point, zero for most.</summary>
    /// <param name="codePoint">The code point.</param>
    /// <returns>The combining class.</returns>
    public static int Combining(int codePoint) =>
        Loaded.Value.Combining.TryGetValue(codePoint, out var value) ? value : 0;

    /// <summary>The name of a code point, or null when it has none.</summary>
    /// <param name="codePoint">The code point.</param>
    /// <returns>The name, or null.</returns>
    public static string? Name(int codePoint)
    {
        if (Loaded.Value.Names.TryGetValue(codePoint, out var name))
        {
            return name;
        }

        if (codePoint >= HangulBase && codePoint < HangulBase + HangulCount)
        {
            var index = codePoint - HangulBase;

            return "HANGUL SYLLABLE "
                + InitialJamo[index / (21 * 28)]
                + MedialJamo[index / 28 % 21]
                + FinalJamo[index % 28];
        }

        foreach (var range in Loaded.Value.Algorithmic)
        {
            if (codePoint >= range.Start && codePoint <= range.End)
            {
                return $"{range.Category}-{codePoint:X4}";
            }
        }

        return null;
    }

    /// <summary>The code point a name refers to, or null when the name is unknown.</summary>
    /// <param name="name">The character name.</param>
    /// <returns>The code point, or null.</returns>
    public static int? Lookup(string name)
    {
        if (Loaded.Value.ByName.TryGetValue(name, out var codePoint))
        {
            return codePoint;
        }

        // The algorithmic names are not in the table, so they are read back apart.
        var mark = name.LastIndexOf('-');

        if (mark > 0
            && int.TryParse(name.AsSpan(mark + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            var prefix = name[..mark];

            foreach (var range in Loaded.Value.Algorithmic)
            {
                if (parsed >= range.Start && parsed <= range.End && range.Category == prefix)
                {
                    return parsed;
                }
            }
        }

        return name.StartsWith("HANGUL SYLLABLE ", StringComparison.Ordinal)
            ? Syllable(name["HANGUL SYLLABLE ".Length..])
            : null;
    }

    /// <summary>Reads a Hangul syllable name back into its code point.</summary>
    private static int? Syllable(string jamo)
    {
        var initial = Longest(InitialJamo, jamo, 0);

        if (initial < 0)
        {
            return null;
        }

        var rest = jamo[InitialJamo[initial].Length..];
        var medial = Longest(MedialJamo, rest, 0);

        if (medial < 0)
        {
            return null;
        }

        var tail = rest[MedialJamo[medial].Length..];
        var final = Array.IndexOf(FinalJamo, tail);

        return final < 0 ? null : HangulBase + (((initial * 21) + medial) * 28) + final;
    }

    /// <summary>The index of the longest table entry that starts <paramref name="text"/>.</summary>
    private static int Longest(string[] table, string text, int fallback)
    {
        var best = -1;

        for (var i = 0; i < table.Length; i++)
        {
            if (table[i].Length > 0
                && text.StartsWith(table[i], StringComparison.Ordinal)
                && (best < 0 || table[i].Length > table[best].Length))
            {
                best = i;
            }
        }

        // An empty entry matches anything, so it is only used when nothing else did.
        return best >= 0 ? best : Array.IndexOf(table, string.Empty) is var empty && empty >= 0 ? empty : fallback - 1;
    }

    /// <summary>
    /// Normalizes text to one of the four Unicode normal forms.
    /// </summary>
    /// <remarks>
    /// Implemented here rather than through the host runtime because the host's answer
    /// depends on which ICU it was built against — and, under an invariant-globalization
    /// build, there is no answer at all. A sandbox that produced different text on
    /// different machines would not be reproducible.
    /// </remarks>
    /// <param name="text">The text to normalize.</param>
    /// <param name="compatibility">True for the K forms, which fold compatibility variants.</param>
    /// <param name="compose">True for the composed forms, C and KC.</param>
    /// <returns>The normalized text.</returns>
    public static string Normalize(string text, bool compatibility, bool compose)
    {
        var decomposed = new List<int>(text.Length);

        foreach (var codePoint in CodePoints(text))
        {
            Decompose(codePoint, compatibility, decomposed);
        }

        Reorder(decomposed);

        if (compose)
        {
            Compose(decomposed);
        }

        var builder = new StringBuilder(decomposed.Count);

        foreach (var codePoint in decomposed)
        {
            builder.Append(char.ConvertFromUtf32(codePoint));
        }

        return builder.ToString();
    }

    /// <summary>The code points of a string, pairing surrogates.</summary>
    private static IEnumerable<int> CodePoints(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                yield return char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
                continue;
            }

            yield return text[i];
        }
    }

    /// <summary>Decomposes one code point into <paramref name="into"/>, recursively.</summary>
    private static void Decompose(int codePoint, bool compatibility, List<int> into)
    {
        // Hangul syllables decompose by arithmetic rather than by table.
        if (codePoint >= HangulBase && codePoint < HangulBase + HangulCount)
        {
            var index = codePoint - HangulBase;
            into.Add(JamoLeadBase + (index / (JamoVowelCount * JamoTailCount)));
            into.Add(JamoVowelBase + (index / JamoTailCount % JamoVowelCount));

            if (index % JamoTailCount != 0)
            {
                into.Add(JamoTailBase + (index % JamoTailCount));
            }

            return;
        }

        if (Loaded.Value.Decompositions.TryGetValue(codePoint, out var mapping)
            && (compatibility || !mapping.IsCompatibility))
        {
            foreach (var part in mapping.Parts)
            {
                Decompose(part, compatibility, into);
            }

            return;
        }

        into.Add(codePoint);
    }

    /// <summary>Puts combining marks into canonical order, which is a stable sort by class.</summary>
    private static void Reorder(List<int> codePoints)
    {
        for (var i = 1; i < codePoints.Count; i++)
        {
            var current = Combining(codePoints[i]);

            if (current == 0)
            {
                continue;
            }

            var j = i;

            while (j > 0 && Combining(codePoints[j - 1]) > current)
            {
                (codePoints[j - 1], codePoints[j]) = (codePoints[j], codePoints[j - 1]);
                j--;
            }
        }
    }

    /// <summary>Composes a canonically ordered sequence in place, as UAX #15 describes.</summary>
    private static void Compose(List<int> codePoints)
    {
        if (codePoints.Count == 0)
        {
            return;
        }

        var starter = 0;
        var last = -1;
        var write = 1;

        for (var read = 1; read < codePoints.Count; read++)
        {
            var current = codePoints[read];
            var combining = Combining(current);

            // A character may combine with the last starter only when nothing blocking sits
            // between them: a mark of the same or a higher class blocks the pairing.
            if ((last < 0 || last < combining) && Pair(codePoints[starter], current) is { } composed)
            {
                codePoints[starter] = composed;
                continue;
            }

            if (combining == 0)
            {
                starter = write;
                last = -1;
            }
            else
            {
                last = combining;
            }

            codePoints[write++] = current;
        }

        codePoints.RemoveRange(write, codePoints.Count - write);
    }

    /// <summary>The primary composite of two code points, or null when there is none.</summary>
    private static int? Pair(int first, int second)
    {
        // Hangul composes by arithmetic, in two steps: lead plus vowel, then plus a tail.
        if (first >= JamoLeadBase && first < JamoLeadBase + 19
            && second >= JamoVowelBase && second < JamoVowelBase + JamoVowelCount)
        {
            return HangulBase
                + ((((first - JamoLeadBase) * JamoVowelCount) + (second - JamoVowelBase)) * JamoTailCount);
        }

        if (first >= HangulBase && first < HangulBase + HangulCount
            && (first - HangulBase) % JamoTailCount == 0
            && second > JamoTailBase && second < JamoTailBase + JamoTailCount)
        {
            return first + (second - JamoTailBase);
        }

        return Loaded.Value.Compositions.TryGetValue(((long)first << 32) | (uint)second, out var composed)
            ? composed
            : null;
    }

    private static Tables Load()
    {
        using var stream = typeof(UnicodeData).GetTypeInfo().Assembly
            .GetManifestResourceStream("Computerwelt.Emulation.Python.Runtime.UnicodeData.bin")
            ?? throw new InvalidOperationException("the Unicode database resource is missing");

        // The blob is zlib-wrapped, which is what a deflate stream with a two-byte header is.
        using var inflate = new ZLibStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(inflate, Encoding.UTF8);

        var version = reader.ReadLine() ?? "0.0.0";
        var categories = new List<Range>();
        var combining = new Dictionary<int, int>();
        var names = new Dictionary<int, string>();
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        var algorithmic = new List<Range>();
        var decompositions = new Dictionary<int, Mapping>();
        var compositions = new Dictionary<long, int>();
        var section = string.Empty;

        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#'))
            {
                section = line;
                continue;
            }

            var fields = line.Split(':');

            switch (section)
            {
                case "#categories":
                    categories.Add(new Range(Hex(fields[0]), Hex(fields[1]), fields[2]));
                    break;

                case "#combining":
                    combining[Hex(fields[0])] = int.Parse(fields[1], CultureInfo.InvariantCulture);
                    break;

                case "#names":
                {
                    var codePoint = Hex(fields[0]);
                    names[codePoint] = fields[1];
                    byName[fields[1]] = codePoint;
                    break;
                }

                case "#algorithmic":
                    algorithmic.Add(new Range(Hex(fields[0]), Hex(fields[1]), fields[2]));
                    break;

                case "#decompositions":
                    decompositions[Hex(fields[0])] = new Mapping(
                        fields[1] == "K",
                        [.. fields[2].Split(' ').Select(Hex)]);

                    break;

                case "#compositions":
                {
                    var pair = fields[0].Split(' ');
                    compositions[((long)Hex(pair[0]) << 32) | (uint)Hex(pair[1])] = Hex(fields[1]);
                    break;
                }
            }
        }

        return new Tables(
            version,
            [.. categories],
            combining,
            names,
            byName,
            [.. algorithmic],
            decompositions,
            compositions);
    }

    private static int Hex(string text) => int.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private readonly record struct Range(int Start, int End, string Category);

    private readonly record struct Mapping(bool IsCompatibility, int[] Parts);

    private sealed record Tables(
        string Version,
        Range[] Categories,
        Dictionary<int, int> Combining,
        Dictionary<int, string> Names,
        Dictionary<string, int> ByName,
        Range[] Algorithmic,
        Dictionary<int, Mapping> Decompositions,
        Dictionary<long, int> Compositions);
}
