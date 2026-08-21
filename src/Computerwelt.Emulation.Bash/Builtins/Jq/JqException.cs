namespace Computerwelt.Emulation.Bash.Builtins.Jq;

/// <summary>
/// An error raised while compiling or running a jq filter.
/// </summary>
/// <remarks>
/// jq's <c>try</c>/<c>catch</c> and the postfix <c>?</c> catch these, so the payload is kept
/// as a <see cref="JsonValue"/> rather than only a message: <c>error({code: 42})</c> must
/// reach a handler as the object it was raised with.
/// </remarks>
internal sealed class JqException : Exception
{
    /// <summary>Raises an error carrying a message.</summary>
    /// <param name="message">The message.</param>
    public JqException(string message)
        : base(message) => Payload = new JsonString(message);

    /// <summary>Raises an error carrying an arbitrary value.</summary>
    /// <param name="payload">The value.</param>
    public JqException(JsonValue payload)
        : base(Describe(payload)) => Payload = payload;

    /// <summary>The value the error carries.</summary>
    public JsonValue Payload { get; }

    private static string Describe(JsonValue payload) =>
        payload is JsonString text ? text.Value : payload.ToString();
}

/// <summary>Thrown by <c>break $label</c> to unwind to the matching <c>label</c>.</summary>
/// <param name="label">The label being broken out of.</param>
internal sealed class JqBreakException(string label) : Exception($"break ${label}")
{
    /// <summary>The label being broken out of.</summary>
    public string Label { get; } = label;
}
