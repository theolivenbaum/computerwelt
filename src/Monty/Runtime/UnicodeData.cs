using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace Monty.Runtime;

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

    private static Tables Load()
    {
        using var stream = typeof(UnicodeData).GetTypeInfo().Assembly
            .GetManifestResourceStream("Monty.Runtime.UnicodeData.bin")
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
            }
        }

        return new Tables(version, [.. categories], combining, names, byName, [.. algorithmic]);
    }

    private static int Hex(string text) => int.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private readonly record struct Range(int Start, int End, string Category);

    private sealed record Tables(
        string Version,
        Range[] Categories,
        Dictionary<int, int> Combining,
        Dictionary<int, string> Names,
        Dictionary<string, int> ByName,
        Range[] Algorithmic);
}
