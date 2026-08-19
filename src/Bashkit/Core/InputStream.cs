namespace Bashkit;

/// <summary>
/// A consumable view over a command's standard input.
/// </summary>
/// <remarks>
/// <para>
/// <c>read</c> is destructive: <c>while read line; do ...; done &lt; file</c> works only
/// because each call takes the next line and leaves the rest. Modelling stdin as an
/// immutable value cannot express that — every read would return the first line forever —
/// so a redirection that feeds a loop creates one of these and the loop's reads share it.
/// </para>
/// <para>
/// The stream is shared by reference for exactly as long as the descriptor would be: a
/// compound command's redirection scopes one to its body, and a piped command gets its own.
/// </para>
/// </remarks>
public sealed class InputStream
{
    private readonly string _text;
    private int _position;

    /// <summary>Creates a stream over <paramref name="text"/>.</summary>
    public InputStream(string text) => _text = text ?? string.Empty;

    /// <summary>True when everything has been consumed.</summary>
    public bool AtEnd => _position >= _text.Length;

    /// <summary>What has not been read yet, without consuming it.</summary>
    public StreamData Remaining => StreamData.FromText(_text[Math.Min(_position, _text.Length)..]);

    /// <summary>
    /// Consumes up to and including the next <paramref name="delimiter"/>, returning the
    /// text before it, or <see langword="null"/> at end of input.
    /// </summary>
    public string? ReadLine(char delimiter) => ReadLine(delimiter, out _);

    /// <summary>
    /// Consumes up to and including the next <paramref name="delimiter"/>.
    /// </summary>
    /// <param name="delimiter">The character that ends a line.</param>
    /// <param name="terminated">
    /// Set to false for a final line that ran out of input before the delimiter, which is
    /// what makes <c>read</c> report failure while still assigning what it found.
    /// </param>
    /// <returns>The text before the delimiter, or <see langword="null"/> at end of input.</returns>
    public string? ReadLine(char delimiter, out bool terminated)
    {
        if (AtEnd)
        {
            terminated = false;
            return null;
        }

        var index = _text.IndexOf(delimiter, _position);

        if (index < 0)
        {
            // A final line with no delimiter is still a line, but an incomplete one.
            var tail = _text[_position..];
            _position = _text.Length;
            terminated = false;
            return tail;
        }

        var line = _text[_position..index];
        _position = index + 1;
        terminated = true;
        return line;
    }

    /// <summary>Consumes and returns everything that is left.</summary>
    public string ReadToEnd()
    {
        var rest = _text[Math.Min(_position, _text.Length)..];
        _position = _text.Length;
        return rest;
    }
}
