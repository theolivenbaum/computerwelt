using System.Text;

namespace Bashkit;

/// <summary>
/// A byte-oriented stdio payload.
/// </summary>
/// <remarks>
/// Shell data is bytes, not text: <c>cat</c> of a PNG piped into <c>wc -c</c> must report
/// the exact byte count, and <c>tr</c> operates on bytes. Upstream models this with
/// <c>Vec&lt;u8&gt;</c> throughout, so the port carries bytes end to end and decodes only
/// at the boundary. Decoding is lossy (invalid sequences become U+FFFD) and never throws.
/// </remarks>
public readonly struct StreamData : IEquatable<StreamData>
{
    private static readonly UTF8Encoding LossyUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly byte[]? _bytes;

    private StreamData(byte[]? bytes) => _bytes = bytes;

    /// <summary>An empty payload.</summary>
    public static StreamData Empty => default;

    /// <summary>The raw bytes.</summary>
    public ReadOnlySpan<byte> Span => _bytes ?? [];

    /// <summary>The raw bytes as memory.</summary>
    public ReadOnlyMemory<byte> Memory => _bytes ?? ReadOnlyMemory<byte>.Empty;

    /// <summary>Byte length of the payload.</summary>
    public int Length => _bytes?.Length ?? 0;

    /// <summary>True when the payload holds no bytes.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>Wraps a UTF-8 encoding of <paramref name="text"/>.</summary>
    public static StreamData FromText(string? text) =>
        string.IsNullOrEmpty(text) ? Empty : new StreamData(LossyUtf8.GetBytes(text));

    /// <summary>Wraps <paramref name="bytes"/> without copying. The array must not be mutated afterwards.</summary>
    public static StreamData FromBytes(byte[]? bytes) =>
        bytes is null || bytes.Length == 0 ? Empty : new StreamData(bytes);

    /// <summary>Copies <paramref name="bytes"/> into a new payload.</summary>
    public static StreamData CopyFrom(ReadOnlySpan<byte> bytes) =>
        bytes.IsEmpty ? Empty : new StreamData(bytes.ToArray());

    /// <summary>Implicitly wraps a string.</summary>
    public static implicit operator StreamData(string? text) => FromText(text);

    /// <summary>Implicitly wraps a byte array.</summary>
    public static implicit operator StreamData(byte[]? bytes) => FromBytes(bytes);

    /// <summary>Concatenates two payloads.</summary>
    public static StreamData Concat(StreamData left, StreamData right)
    {
        if (left.IsEmpty)
        {
            return right;
        }

        if (right.IsEmpty)
        {
            return left;
        }

        var buffer = new byte[left.Length + right.Length];
        left.Span.CopyTo(buffer);
        right.Span.CopyTo(buffer.AsSpan(left.Length));
        return new StreamData(buffer);
    }

    /// <summary>Concatenates two payloads.</summary>
    public static StreamData operator +(StreamData left, StreamData right) => Concat(left, right);

    /// <summary>Returns the first <paramref name="count"/> bytes.</summary>
    public StreamData Truncate(int count) =>
        count >= Length ? this : CopyFrom(Span[..Math.Max(0, count)]);

    /// <summary>Copies the payload into a fresh array.</summary>
    public byte[] ToArray() => _bytes is null ? [] : (byte[])_bytes.Clone();

    /// <summary>Decodes the payload as UTF-8, replacing invalid sequences.</summary>
    public override string ToString() => _bytes is null ? string.Empty : LossyUtf8.GetString(_bytes);

    /// <summary>Splits the payload into lines, keeping no trailing empty element for a final newline.</summary>
    public string[] ToLines()
    {
        var text = ToString();
        if (text.Length == 0)
        {
            return [];
        }

        if (text.EndsWith('\n'))
        {
            text = text[..^1];
        }

        return text.Split('\n');
    }

    /// <inheritdoc />
    public bool Equals(StreamData other) => Span.SequenceEqual(other.Span);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StreamData other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Span);
        return hash.ToHashCode();
    }

    /// <summary>Compares two payloads byte-for-byte.</summary>
    public static bool operator ==(StreamData left, StreamData right) => left.Equals(right);

    /// <summary>Compares two payloads byte-for-byte.</summary>
    public static bool operator !=(StreamData left, StreamData right) => !left.Equals(right);
}
