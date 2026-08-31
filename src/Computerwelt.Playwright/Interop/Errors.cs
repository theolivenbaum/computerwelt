using Computerwelt.Emulation.Python.Runtime;
using Microsoft.Playwright;

namespace Computerwelt.Playwright.Interop;

/// <summary>
/// The exception classes <c>playwright.sync_api</c> exports, and the translation into them.
/// </summary>
/// <remarks>
/// Nothing the driver throws may reach a script as a host exception: a
/// <see cref="PlaywrightException"/> escaping into the sandbox would be a .NET type crossing
/// a boundary that only carries Python values, and a script could neither catch nor print
/// it. Every call therefore goes through <see cref="Translate"/>.
/// </remarks>
internal static class Errors
{
    /// <summary>
    /// <c>playwright.sync_api.Error</c> — the base of everything the driver reports.
    /// </summary>
    /// <remarks>
    /// One instance for the process, not one per run. Two runs never compare exception
    /// classes with each other, but one run that imports the module twice must see one
    /// class, or <c>except Error</c> would miss what <c>raise Error</c> threw.
    /// </remarks>
    public static PyExceptionType Error { get; } = PyExceptionType.DefineHostException("Error");

    /// <summary><c>playwright.sync_api.TimeoutError</c>, which derives from <c>Error</c> as upstream's does.</summary>
    public static PyExceptionType TimeoutError { get; } = PyExceptionType.DefineHostException("TimeoutError", Error);

    /// <summary>Raises <c>Error</c> with <paramref name="message"/>.</summary>
    public static PyRaise Fail(string message) => new(new PyException(Error, message));

    /// <summary>Raises <c>TimeoutError</c> with <paramref name="message"/>.</summary>
    public static PyRaise Timeout(string message) => new(new PyException(TimeoutError, message));

    /// <summary>
    /// Turns whatever the driver threw into the Python exception a script expects.
    /// </summary>
    /// <remarks>
    /// A <see cref="PyRaise"/> passes straight through — it is already a Python error, and
    /// usually one this package raised on the way in, such as a refused navigation.
    /// </remarks>
    public static Exception Translate(Exception exception) => exception switch
    {
        PyRaise raise => raise,

        // A wait that ran out is the one failure scripts branch on, so it gets its own
        // class. The driver reports it as the framework's own timeout type; the wording
        // check covers the cases it reports through its own type instead.
        System.TimeoutException timeout => Timeout(Clean(timeout.Message)),
        PlaywrightException driver when IsTimeout(driver.Message) => Timeout(Clean(driver.Message)),
        PlaywrightException driver => Fail(Clean(driver.Message)),

        // A session disposed under a running script, and a cap this package enforces: both
        // are conditions the script can see and neither is a bug in it. The disposed case is
        // matched first because it derives from the other.
        ObjectDisposedException => Fail("the browser session has been closed by the host"),
        InvalidOperationException invalid => Fail(invalid.Message),
        OperationCanceledException => Fail("the operation was cancelled by the host"),

        // Total by construction. Whatever else the driver or this package throws, a
        // sandboxed program must be able to catch it and print it, so it arrives as `Error`
        // rather than as a .NET type crossing a boundary that carries only Python values.
        // The type name is kept in the message, so a genuine host bug is still identifiable
        // in the traceback instead of being disguised as a browser failure.
        _ => Fail($"{exception.GetType().Name}: {Clean(exception.Message)}"),
    };

    /// <summary>
    /// Trims the driver's message to the part a script can act on.
    /// </summary>
    /// <remarks>
    /// Playwright appends a call log — dozens of lines of internal retries — to most
    /// failures. It is invaluable when a person is watching and noise when a program is
    /// deciding, so the first paragraph is kept and the log is dropped.
    /// </remarks>
    /// <summary>
    /// Whether the driver's message reports a timeout.
    /// </summary>
    /// <remarks>
    /// Playwright's .NET binding raises one exception type for everything, and the wording
    /// is the only thing that distinguishes a wait that ran out from a selector that was
    /// malformed. Both spellings the driver uses are matched here.
    /// </remarks>
    private static bool IsTimeout(string message) =>
        message.Contains("Timeout", StringComparison.Ordinal)
        && (message.Contains("exceeded", StringComparison.Ordinal)
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase));

    private static string Clean(string message)
    {
        var index = message.IndexOf("\nCall log:", StringComparison.Ordinal);
        var text = index >= 0 ? message[..index] : message;

        return text.Trim();
    }
}
