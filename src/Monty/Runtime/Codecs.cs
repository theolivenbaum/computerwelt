using System.Text;

namespace Monty.Runtime;

/// <summary>
/// The text codecs <c>str.encode</c>, <c>bytes.decode</c> and the <c>codecs</c> module
/// share.
/// </summary>
/// <remarks>
/// The encoders and decoders are written out by hand rather than delegated to
/// <see cref="Encoding"/> because the observable behaviour is the error reporting: CPython
/// names a codec, a byte position and a reason, splits invalid input into maximal subparts,
/// and consults the error handler only once something actually fails. A framework encoder
/// with <c>EncoderFallback</c> can produce the right bytes but none of that.
/// </remarks>
public static class Codecs
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["utf_8"] = "utf-8", ["utf8"] = "utf-8", ["utf"] = "utf-8", ["u8"] = "utf-8",
        ["cp65001"] = "utf-8", ["utf8_ucs2"] = "utf-8", ["utf8_ucs4"] = "utf-8",

        ["utf_8_sig"] = "utf-8-sig",

        ["ascii"] = "ascii", ["646"] = "ascii", ["us"] = "ascii", ["us_ascii"] = "ascii",
        ["cp367"] = "ascii", ["ibm367"] = "ascii", ["csascii"] = "ascii",
        ["ansi_x3.4_1968"] = "ascii", ["ansi_x3.4_1986"] = "ascii",
        ["iso646_us"] = "ascii", ["iso_646.irv:1991"] = "ascii", ["iso_ir_6"] = "ascii",

        ["latin_1"] = "latin-1", ["latin1"] = "latin-1", ["latin"] = "latin-1",
        ["l1"] = "latin-1", ["iso_8859_1"] = "latin-1", ["iso8859_1"] = "latin-1",
        ["8859"] = "latin-1", ["cp819"] = "latin-1",

        ["utf_16"] = "utf-16", ["utf16"] = "utf-16", ["u16"] = "utf-16",
        ["utf_16_le"] = "utf-16-le", ["utf_16le"] = "utf-16-le",
        ["unicodelittleunmarked"] = "utf-16-le",
        ["utf_16_be"] = "utf-16-be", ["utf_16be"] = "utf-16-be",
        ["unicodebigunmarked"] = "utf-16-be",

        ["utf_32"] = "utf-32", ["utf32"] = "utf-32", ["u32"] = "utf-32",
        ["utf_32_le"] = "utf-32-le", ["utf_32le"] = "utf-32-le",
        ["utf_32_be"] = "utf-32-be", ["utf_32be"] = "utf-32-be",
    };

    /// <summary>
    /// Resolves an encoding name to its canonical form, or raises <c>LookupError</c>.
    /// </summary>
    /// <remarks>
    /// CPython lowercases the name and folds runs of spaces and hyphens into one
    /// underscore, but keeps dots — which is why <c>utf.8</c> is not <c>utf-8</c>. The
    /// error quotes the name as given, not the normalised one.
    /// </remarks>
    public static string Resolve(string name)
    {
        var normalized = new StringBuilder(name.Length);

        foreach (var c in name.ToLowerInvariant())
        {
            if (c is ' ' or '-' or '_')
            {
                if (normalized.Length > 0 && normalized[^1] != '_')
                {
                    normalized.Append('_');
                }

                continue;
            }

            normalized.Append(c);
        }

        var key = normalized.ToString().Trim('_');

        return Aliases.TryGetValue(key, out var codec)
            ? codec
            : throw new PyRaise(new PyException(PyExceptionType.LookupError, $"unknown encoding: {name}"));
    }

    /// <summary>Encodes text, applying <paramref name="errors"/> to whatever will not fit.</summary>
    public static byte[] Encode(string text, string encoding, string errors)
    {
        var codec = Resolve(encoding);

        return codec switch
        {
            "ascii" => EncodeNarrow(text, codec, errors, 0x7f),
            "latin-1" => EncodeNarrow(text, codec, errors, 0xff),
            "utf-16" => [.. (byte[])[0xff, 0xfe], .. EncodeUnits(text, littleEndian: true, 2)],
            "utf-16-le" => EncodeUnits(text, littleEndian: true, 2),
            "utf-16-be" => EncodeUnits(text, littleEndian: false, 2),
            "utf-32" => [.. (byte[])[0xff, 0xfe, 0x00, 0x00], .. EncodeUnits(text, littleEndian: true, 4)],
            "utf-32-le" => EncodeUnits(text, littleEndian: true, 4),
            "utf-32-be" => EncodeUnits(text, littleEndian: false, 4),

            // The `-sig` variant writes the UTF-8 signature, which is the BOM's code point
            // spelled in UTF-8 rather than a byte-order marker — UTF-8 has no order.
            "utf-8-sig" => [.. (byte[])[0xef, 0xbb, 0xbf], .. Encoding.UTF8.GetBytes(text)],
            _ => Encoding.UTF8.GetBytes(text),
        };
    }

    /// <summary>Decodes bytes, applying <paramref name="errors"/> to whatever will not parse.</summary>
    public static string Decode(byte[] value, string encoding, string errors)
    {
        var codec = Resolve(encoding);
        var offset = 0;

        // The BOM-consuming variants resolve to a concrete endianness, and errors are
        // reported under that name — with positions that still count the BOM.
        if (codec == "utf-16")
        {
            (codec, offset) = value.Length >= 2 && value[0] == 0xfe && value[1] == 0xff
                ? ("utf-16-be", 2)
                : value.Length >= 2 && value[0] == 0xff && value[1] == 0xfe ? ("utf-16-le", 2)
                : ("utf-16-le", 0);
        }
        else if (codec == "utf-32")
        {
            (codec, offset) = value.Length >= 4 && value[0] == 0 && value[1] == 0 && value[2] == 0xfe && value[3] == 0xff
                ? ("utf-32-be", 4)
                : value.Length >= 4 && value[0] == 0xff && value[1] == 0xfe && value[2] == 0 && value[3] == 0
                    ? ("utf-32-le", 4)
                    : ("utf-32-le", 0);
        }

        // The `-sig` variant drops a leading signature if there is one, and is plain
        // UTF-8 otherwise.
        if (codec == "utf-8-sig")
        {
            codec = "utf-8";

            if (value.Length >= 3 && value[0] == 0xef && value[1] == 0xbb && value[2] == 0xbf)
            {
                value = value[3..];
            }
        }

        return codec switch
        {
            "ascii" => DecodeNarrow(value, codec, errors, 0x7f),
            "latin-1" => DecodeNarrow(value, codec, errors, 0xff),
            "utf-16-le" or "utf-16-be" => DecodeUtf16(value, codec, errors, offset),
            "utf-32-le" or "utf-32-be" => DecodeUtf32(value, codec, errors, offset),
            _ => DecodeUtf8(value, errors),
        };
    }

    // ---- encoders ----

    private static byte[] EncodeNarrow(string text, string codec, string errors, int limit)
    {
        var bytes = new List<byte>(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] <= limit)
            {
                bytes.Add((byte)text[i++]);
                continue;
            }

            // CPython reports one error for a whole run of unencodable characters, and the
            // handler replaces the run as a unit.
            var start = i;

            while (i < text.Length && text[i] > limit)
            {
                i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            }

            bytes.AddRange(HandleEncodeError(text, codec, errors, start, i, limit));
        }

        return [.. bytes];
    }

    private static byte[] EncodeUnits(string text, bool littleEndian, int width)
    {
        var bytes = new List<byte>(text.Length * width);

        if (width == 2)
        {
            foreach (var unit in text)
            {
                Append(bytes, unit, littleEndian, 2);
            }

            return [.. bytes];
        }

        for (var i = 0; i < text.Length; i++)
        {
            var code = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                ? char.ConvertToUtf32(text[i], text[++i])
                : text[i];

            Append(bytes, code, littleEndian, 4);
        }

        return [.. bytes];
    }

    private static void Append(List<byte> bytes, int value, bool littleEndian, int width)
    {
        for (var i = 0; i < width; i++)
        {
            bytes.Add((byte)(value >> (8 * (littleEndian ? i : width - 1 - i))));
        }
    }

    /// <summary>Applies an error handler to a run of unencodable characters.</summary>
    private static byte[] HandleEncodeError(string text, string codec, string errors, int start, int end, int limit)
    {
        switch (errors)
        {
            case "ignore":
                return [];

            case "replace":
                return Encoding.ASCII.GetBytes(new string('?', CountCharacters(text, start, end)));

            case "backslashreplace":
                return Encoding.ASCII.GetBytes(Escape(text[start..end]));

            case "xmlcharrefreplace":
            {
                var builder = new StringBuilder();

                for (var i = start; i < end; i++)
                {
                    var code = char.IsHighSurrogate(text[i]) && i + 1 < end
                        ? char.ConvertToUtf32(text[i], text[++i])
                        : text[i];

                    builder.Append("&#").Append(code).Append(';');
                }

                return Encoding.ASCII.GetBytes(builder.ToString());
            }

            case "namereplace":
            {
                var builder = new StringBuilder();

                for (var i = start; i < end; i++)
                {
                    var code = char.IsHighSurrogate(text[i]) && i + 1 < end
                        ? char.ConvertToUtf32(text[i], text[++i])
                        : text[i];

                    // A character with no name — a C1 control, say — falls back to the
                    // backslash escape, since there is no `\N{...}` to write.
                    builder.Append(UnicodeData.Name(code) is { } name
                        ? $"\\N{{{name}}}"
                        : Escape(char.ConvertFromUtf32(code)));
                }

                return Encoding.ASCII.GetBytes(builder.ToString());
            }

            // These only rescue lone surrogates, which cannot appear here, so they behave
            // exactly as `strict` does.
            case "strict" or "surrogateescape" or "surrogatepass":
                break;

            default:
                throw new PyRaise(new PyException(
                    PyExceptionType.LookupError, $"unknown error handler name '{errors}'"));
        }

        var characters = CountCharacters(text, start, end);
        var where = characters == 1
            ? $"character '{Escape(text[start..end])}' in position {start}"
            : $"characters in position {start}-{end - 1}";

        throw new PyRaise(new PyException(
            PyExceptionType.UnicodeEncodeError,
            $"'{codec}' codec can't encode {where}: ordinal not in range({limit + 1})"));
    }

    private static int CountCharacters(string text, int start, int end)
    {
        var count = 0;

        for (var i = start; i < end; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < end)
            {
                i++;
            }

            count++;
        }

        return count;
    }

    /// <summary>Renders text as the backslash escapes <c>backslashreplace</c> produces.</summary>
    private static string Escape(string text)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            var code = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                ? char.ConvertToUtf32(text[i], text[++i])
                : text[i];

            builder.Append(code switch
            {
                <= 0xff => $"\\x{code:x2}",
                <= 0xffff => $"\\u{code:x4}",
                _ => $"\\U{code:x8}",
            });
        }

        return builder.ToString();
    }

    // ---- decoders ----

    private static string DecodeNarrow(byte[] value, string codec, string errors, int limit)
    {
        var text = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] <= limit)
            {
                text.Append((char)value[i]);
                continue;
            }

            text.Append(HandleDecodeError(value, codec, errors, i, i + 1, "ordinal not in range(128)"));
        }

        return text.ToString();
    }

    private static string DecodeUtf16(byte[] value, string codec, string errors, int offset)
    {
        var little = codec.EndsWith("le", StringComparison.Ordinal);
        var text = new StringBuilder();
        var i = offset;

        while (i < value.Length)
        {
            if (i + 1 >= value.Length)
            {
                text.Append(HandleDecodeError(value, codec, errors, i, i + 1, "truncated data"));
                break;
            }

            var unit = little ? value[i] | (value[i + 1] << 8) : (value[i] << 8) | value[i + 1];

            if (!char.IsSurrogate((char)unit))
            {
                text.Append((char)unit);
                i += 2;
                continue;
            }

            if (char.IsLowSurrogate((char)unit))
            {
                text.Append(HandleDecodeError(value, codec, errors, i, i + 2, "illegal encoding"));
                i += 2;
                continue;
            }

            // A high surrogate needs a low one after it; a short tail is "unexpected end of
            // data" and takes the stray bytes with it as one error unit.
            if (i + 3 >= value.Length)
            {
                text.Append(HandleDecodeError(value, codec, errors, i, value.Length, "unexpected end of data"));
                break;
            }

            var next = little ? value[i + 2] | (value[i + 3] << 8) : (value[i + 2] << 8) | value[i + 3];

            if (!char.IsLowSurrogate((char)next))
            {
                text.Append(HandleDecodeError(value, codec, errors, i, i + 2, "illegal UTF-16 surrogate"));
                i += 2;
                continue;
            }

            text.Append((char)unit).Append((char)next);
            i += 4;
        }

        return text.ToString();
    }

    private static string DecodeUtf32(byte[] value, string codec, string errors, int offset)
    {
        var little = codec.EndsWith("le", StringComparison.Ordinal);
        var text = new StringBuilder();
        var i = offset;

        while (i < value.Length)
        {
            if (i + 3 >= value.Length)
            {
                text.Append(HandleDecodeError(value, codec, errors, i, value.Length, "truncated data"));
                break;
            }

            var code = little
                ? value[i] | (value[i + 1] << 8) | (value[i + 2] << 16) | (value[i + 3] << 24)
                : (value[i] << 24) | (value[i + 1] << 16) | (value[i + 2] << 8) | value[i + 3];

            var reason = code is >= 0xd800 and < 0xe000
                ? "code point in surrogate code point range(0xd800, 0xe000)"
                : code is < 0 or > 0x10ffff ? "code point not in range(0x110000)"
                : null;

            text.Append(reason is null
                ? char.ConvertFromUtf32(code)
                : HandleDecodeError(value, codec, errors, i, i + 4, reason));

            i += 4;
        }

        return text.ToString();
    }

    /// <summary>
    /// Decodes UTF-8, splitting invalid input into CPython's maximal subparts.
    /// </summary>
    private static string DecodeUtf8(byte[] value, string errors)
    {
        var text = new StringBuilder(value.Length);
        var i = 0;

        while (i < value.Length)
        {
            var lead = value[i];

            if (lead < 0x80)
            {
                text.Append((char)lead);
                i++;
                continue;
            }

            var length = lead switch
            {
                >= 0xc2 and <= 0xdf => 2,
                >= 0xe0 and <= 0xef => 3,
                >= 0xf0 and <= 0xf4 => 4,
                _ => 0,
            };

            if (length == 0)
            {
                text.Append(HandleDecodeError(value, "utf-8", errors, i, i + 1, "invalid start byte"));
                i++;
                continue;
            }

            // A subpart is as long as the bytes that could still complete the sequence; the
            // first byte that could not is where the error ends.
            var taken = 1;

            while (taken < length && i + taken < value.Length && Continues(lead, value[i + taken], taken))
            {
                taken++;
            }

            if (taken == length)
            {
                var code = lead & (0xff >> (length + 1));

                for (var k = 1; k < length; k++)
                {
                    code = (code << 6) | (value[i + k] & 0x3f);
                }

                text.Append(char.ConvertFromUtf32(code));
                i += length;
                continue;
            }

            var reason = i + taken >= value.Length ? "unexpected end of data" : "invalid continuation byte";
            text.Append(HandleDecodeError(value, "utf-8", errors, i, i + taken, reason));
            i += taken;
        }

        return text.ToString();
    }

    /// <summary>Whether a byte can continue a sequence, given its lead byte and position.</summary>
    private static bool Continues(byte lead, byte b, int position)
    {
        // The first continuation byte is restricted for the sequences that would otherwise
        // admit an overlong form or a surrogate; the rest accept the whole range.
        var (low, high) = (position, lead) switch
        {
            (1, 0xe0) => (0xa0, 0xbf),
            (1, 0xed) => (0x80, 0x9f),
            (1, 0xf0) => (0x90, 0xbf),
            (1, 0xf4) => (0x80, 0x8f),
            _ => (0x80, 0xbf),
        };

        return b >= low && b <= high;
    }

    /// <summary>Applies an error handler to a run of undecodable bytes.</summary>
    private static string HandleDecodeError(
        byte[] value, string codec, string errors, int start, int end, string reason)
    {
        switch (errors)
        {
            case "ignore":
                return string.Empty;

            case "replace":
                return "�";

            case "backslashreplace":
                return string.Concat(value[start..end].Select(static b => $"\\x{b:x2}"));

            // Only a surrogate the codec could otherwise not represent is rescued, and no
            // decoder here produces one, so this re-raises exactly as `strict` does.
            case "strict" or "surrogatepass" or "surrogateescape":
                break;

            case "xmlcharrefreplace" or "namereplace":
                throw new PyRaise(PyErrors.TypeError(
                    "don't know how to handle UnicodeDecodeError in error callback"));

            default:
                throw new PyRaise(new PyException(
                    PyExceptionType.LookupError, $"unknown error handler name '{errors}'"));
        }

        var where = end - start == 1
            ? $"byte 0x{value[start]:x2} in position {start}"
            : $"bytes in position {start}-{end - 1}";

        throw new PyRaise(new PyException(
            PyExceptionType.UnicodeDecodeError, $"'{codec}' codec can't decode {where}: {reason}"));
    }
}
